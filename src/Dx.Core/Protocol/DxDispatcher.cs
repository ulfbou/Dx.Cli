using Dapper;

using Dx.Core.Execution;
using Dx.Core.Execution.Adapters;
using Dx.Core.Execution.Results;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

using System.Diagnostics;
using System.Text;

namespace Dx.Core.Protocol;

/// <summary>
/// The authoritative protocol engine responsible for the atomic execution 
/// of DX documents and the maintenance of the session audit trail.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Protocol Authority:</strong>
/// This type is the sole authority for executing DX documents and producing 
/// authoritative session audit records.
/// </para>
/// <para>
/// <strong>Invariants:</strong>
/// <list type="bullet">
/// <item>
/// <description>No execution may occur outside this dispatcher.</description>
/// </item>
/// <item>
/// <description>Every execution attempt produces exactly two audit log entries 
/// (input intent, execution result).</description>
/// </item>
/// <item>
/// <description>This type must not interpret CLI-specific semantics.</description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public sealed class DxDispatcher(
        SqliteConnection connection,
        IDxStore store,
        string workspaceRoot,
        IgnoreSet ignoreSet,
        string sessionId,
        IDxLogger? logger = null)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly IDxStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
    private readonly IgnoreSet _ignoreSet = ignoreSet ?? throw new ArgumentNullException(nameof(ignoreSet));
    private readonly string _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
    private readonly IDxLogger _logger = logger ?? NullDxLogger.Instance;

    /// <summary>
    /// Dispatches an execution request according to the transactional protocol.
    /// This is the single, authoritative entry point for all DX document execution.
    /// </summary>
    public async Task<DxResult> DispatchAsync(DxExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (document, rawText, direction, _, isDryRun, progress, options, ct) = request;

        // 1. Authoritative Input Logging (Pre-Execution)
        await _store.WriteSessionLogAsync(new SessionLogEntry
        {
            SessionId = _sessionId,
            Direction = direction,
            Document = rawText,
            TxSuccess = null,
            SnapHandle = null
        }, ct);

        DxResult result;

        try
        {
            if (!document.IsMutating)
            {
                _logger.Debug("Document is read-only.");
                var roOps = new List<OperationResult>();

                foreach (var block in document.Blocks)
                {
                    await DispatchReadOnlyBlockAsync(block, roOps, options, ct);
                }

                result = new DxResult(DxResultStatus.Success, null, null, null, isDryRun, null, roOps);
            }
            else
            {
                var dispatchResult = await ExecuteMutatingTransactionAsync(document, isDryRun, progress, options, ct);
                result = DxResultMapper.ToDxResult(dispatchResult, isDryRun);
            }
        }
        catch (Exception ex)
        {
            result = new DxResult(DxResultStatus.ExecutionFailure, ex.Message, null, null, isDryRun, null, null);
        }

        // 2. Authoritative Output Logging (Post-Execution)
        string resultDocument = DxResultLoggingSerializer.Serialize(result);

        await _store.WriteSessionLogAsync(new SessionLogEntry
        {
            SessionId = _sessionId,
            Direction = direction,
            Document = resultDocument,
            TxSuccess = result.IsSuccess ? 1 : 0,
            SnapHandle = result.SnapId
        }, ct);

        return result;
    }

    /// <summary>
    /// Executes a mutating transaction on the specified document, applying all mutation and run blocks, and commits the
    /// resulting state to the workspace.
    /// </summary>
    /// <remarks>This method acquires a workspace lock to ensure exclusive access during the transaction. If
    /// the document's base does not match the current workspace state, the transaction may be rejected or a warning
    /// issued, depending on the provided options. Failure logs are recorded even if the transaction is aborted,
    /// ensuring audit continuity.</remarks>
    /// <param name="document">The document containing mutation and run blocks to be applied as part of the transaction.</param>
    /// <param name="dryRun">If <see langword="true"/>, simulates the transaction without persisting any changes or creating a log entry;
    /// otherwise, applies and commits the mutations.</param>
    /// <param name="progress">An optional progress reporter that receives status messages as the transaction progresses. May be <see
    /// langword="null"/>.</param>
    /// <param name="options">Optional settings that control transaction behavior, such as base mismatch handling and run block timeouts. May
    /// be <see langword="null"/>.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the transaction operation.</param>
    /// <returns>A <see cref="DispatchResult"/> indicating the outcome of the transaction, including success status, resulting
    /// handle, error messages, and a list of operation results.</returns>
    /// <exception cref="DxException">Thrown if a run block fails during the commit-gate phase or if an invalid argument is encountered in a run
    /// block.</exception>
    private async Task<DispatchResult> ExecuteMutatingTransactionAsync(
                DxDocument document,
                bool dryRun,
                IProgress<string>? progress,
                ApplyOptions? options,
                CancellationToken ct)
    {
        var lockFile = Path.Combine(_workspaceRoot, ".dx", "snaps.lock");
        await using var dxLock = await DxLock.AcquireAsync(lockFile, TimeSpan.FromSeconds(5), ct);

        var coordinator = new TransactionCoordinator(_connection, _workspaceRoot, _ignoreSet, _logger);
        var operations = new List<OperationResult>();

        return await coordinator.RunAsync(_sessionId, async (tx, innerCt) =>
        {
            var currentHead = await GetCurrentHeadAsync(innerCt);

            if (document.Header.Base is { } baseHandle)
            {
                var baseHash = HandleAssigner.Resolve(_connection, _sessionId, baseHandle);
                if (baseHash == null || !DxHash.Equal(baseHash, currentHead))
                {
                    var actual = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHead) ?? "?";
                    var message = $"Base mismatch. Expected: {baseHandle}, Actual: {actual}";

                    if (options?.OnBaseMismatch?.ToLowerInvariant() != "warn")
                    {
                        return new DispatchResult(false, null, message, operations, IsBaseMismatch: true);
                    }
                    _logger.Warn(message);
                }
            }

            if (dryRun) return new DispatchResult(true, null, null, operations);

            foreach (var block in document.Blocks.Where(IsMutating))
            {
                innerCt.ThrowIfCancellationRequested();
                await DispatchMutationBlockAsync(block, operations, innerCt);
            }

            var runTimeout = options?.RunTimeoutSeconds ?? 0;
            foreach (var run in document.Blocks.OfType<RequestBlock>().Where(r => r.Type == "run"))
            {
                innerCt.ThrowIfCancellationRequested();
                var (exitCode, output) = await ExecuteRunAsync(run.Body.Trim(), runTimeout, innerCt);
                operations.Add(OperationResult.Create("REQUEST:run", null, exitCode == 0, output));
                if (exitCode != 0) throw new DxException(DxError.InvalidArgument, $"Gate failed: {output}");
            }

            var manifest = ManifestBuilder.Build(_workspaceRoot, _ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            string newHandle;
            bool isCheckout = document.Blocks.Any(b => b is FsBlock fs && fs.Op == "checkout");

            if (DxHash.Equal(snapHash, currentHead) && !isCheckout)
            {
                newHandle = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHead)!;
            }
            else
            {
                var writer = new SnapshotWriter(_connection);
                newHandle = await writer.PersistAsync(_sessionId, snapHash, manifest, innerCt, tx);
            }

            _logger.Info($"→ {newHandle}");
            return new DispatchResult(true, newHandle, null, operations);
        }, ct);
    }

    private async Task DispatchMutationBlockAsync(DxBlock block, List<OperationResult> ops, CancellationToken ct)
    {
        switch (block)
        {
            case FileBlock fb when !fb.ReadOnly:
                await WriteFileAsync(fb, ct);
                ops.Add(OperationResult.SuccessResult("FILE", fb.Path));
                break;
            case PatchBlock pb:
                await ApplyPatchAsync(pb, ct);
                ops.Add(OperationResult.SuccessResult("PATCH", pb.Path, $"{pb.Hunks.Count} hunk(s)"));
                break;
            case FsBlock fs:
                await ExecuteFsOpAsync(fs, ct);
                ops.Add(OperationResult.SuccessResult($"FS:{fs.Op}", fs.Args.GetValueOrDefault("path") ?? fs.Args.GetValueOrDefault("snap")));
                break;
        }
    }

    private async Task DispatchReadOnlyBlockAsync(DxBlock block, List<OperationResult> ops, ApplyOptions? options, CancellationToken ct)
    {
        if (block is not RequestBlock req) return;

        switch (req.Type)
        {
            case "run":
                var runTimeout = options?.RunTimeoutSeconds ?? 0;
                var (exit, output) = await ExecuteRunAsync(req.Body.Trim(), runTimeout, ct);
                ops.Add(OperationResult.Create("REQUEST:run", null, exit == 0, output));
                break;
            default:
                ops.Add(OperationResult.SuccessResult($"REQUEST:{req.Type}", null));
                break;
        }
    }

    private async Task WriteFileAsync(FileBlock fb, CancellationToken ct)
    {
        var absPath = ResolveAndValidate(fb.Path);

        if (!fb.Create && !File.Exists(absPath))
            throw new DxException(DxError.InvalidArgument, $"File does not exist and create=false: {fb.Path}");

        if (fb.IfContains is { } ic && File.Exists(absPath))
        {
            var content = await File.ReadAllTextAsync(absPath, ct);
            if (!content.Contains(ic, StringComparison.Ordinal))
                throw new DxException(DxError.InvalidArgument, $"Precondition failed (if-contains): \"{ic}\" not found in {fb.Path}");
        }

        if (fb.IfLine is { } il)
        {
            var parts = il.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var lineNo) && File.Exists(absPath))
            {
                var lines = await File.ReadAllLinesAsync(absPath, ct);
                if (lineNo < 1 || lineNo > lines.Length || !lines[lineNo - 1].Contains(parts[1], StringComparison.Ordinal))
                    throw new DxException(DxError.InvalidArgument, $"Precondition failed (if-line): line {lineNo} does not match \"{parts[1]}\"");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        var encoding = ResolveEncoding(fb.Encoding);
        await File.WriteAllTextAsync(absPath, fb.Content, encoding, ct);

        _logger.Debug($"  write {fb.Path}");
    }

    private async Task ApplyPatchAsync(PatchBlock pb, CancellationToken ct)
    {
        var absPath = ResolveAndValidate(pb.Path);

        if (!File.Exists(absPath))
            throw new DxException(DxError.InvalidArgument, $"Cannot patch non-existent file: {pb.Path}");

        var content = await File.ReadAllTextAsync(absPath, ct);
        var patched = PatchEngine.Apply(content, pb.Hunks);
        await File.WriteAllTextAsync(absPath, patched, ct);

        _logger.Debug($"  patch {pb.Path} ({pb.Hunks.Count} hunks)");
    }

    private async Task ExecuteFsOpAsync(FsBlock fs, CancellationToken ct)
    {
        switch (fs.Op)
        {
            case "move":
                {
                    var from = ResolveAndValidate(fs.Args.GetValueOrDefault("from", ""));
                    var to = ResolveAndValidate(fs.Args.GetValueOrDefault("to", ""));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                    _logger.Debug($"  move {fs.Args["from"]} → {fs.Args["to"]}");
                    break;
                }
            case "delete":
                {
                    var path = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                    var recursive = fs.Args.GetValueOrDefault("recursive", "false") == "true";
                    var ifExists = fs.Args.GetValueOrDefault("if-exists", "false") == "true";

                    if (Directory.Exists(path))
                    {
                        if (!recursive)
                            throw new DxException(DxError.InvalidArgument, $"Cannot delete directory without recursive=true: {fs.Args["path"]}");
                        Directory.Delete(path, recursive: true);
                    }
                    else if (File.Exists(path))
                        File.Delete(path);
                    else if (!ifExists)
                        throw new DxException(DxError.InvalidArgument, $"Path not found: {fs.Args["path"]}");

                    _logger.Debug($"  delete {fs.Args["path"]}");
                    break;
                }
            case "encode":
                {
                    var path = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                    var toEncoding = fs.Args.GetValueOrDefault("to", "utf-8");
                    var lineEndings = fs.Args.GetValueOrDefault("line-endings", "preserve");

                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    var srcEnc = DetectEncoding(bytes);
                    var text = srcEnc.GetString(StripBom(bytes, srcEnc));

                    if (lineEndings != "preserve")
                        text = NormalizeLineEndings(text, lineEndings);

                    var (dstEnc, addBom) = ResolveEncodingWithBom(toEncoding);
                    var outBytes = dstEnc.GetBytes(text);

                    using var fs2 = File.OpenWrite(path);
                    if (addBom)
                    {
                        var bom = dstEnc.GetPreamble();
                        await fs2.WriteAsync(bom, ct);
                    }
                    await fs2.WriteAsync(outBytes, ct);
                    fs2.SetLength(fs2.Position);

                    _logger.Debug($"  encode {fs.Args["path"]} → {toEncoding}");
                    break;
                }
            case "checkout":
                {
                    var snapHandle = fs.Args.GetValueOrDefault("snap", "")
                        ?? throw new DxException(DxError.InvalidArgument, "checkout requires snap= argument");

                    var snapHash = HandleAssigner.Resolve(_connection, _sessionId, snapHandle)
                        ?? throw new DxException(DxError.SnapNotFound, $"Snap not found: {snapHandle}");

                    var engine = new RollbackEngine(_connection, _workspaceRoot, _ignoreSet);
                    await Task.Run(() => engine.RestoreTo(snapHash), ct);

                    _logger.Debug($"  checkout {snapHandle}");
                    break;
                }
            case "restore":
                {
                    var path = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                    var snapHandle = fs.Args.GetValueOrDefault("snap", "")
                        ?? throw new DxException(DxError.InvalidArgument, "restore requires snap= argument");

                    var snapHash = HandleAssigner.Resolve(_connection, _sessionId, snapHandle)
                        ?? throw new DxException(DxError.SnapNotFound, $"Snap not found: {snapHandle}");

                    var relPath = DxPath.Normalize(_workspaceRoot, path);
                    var fileHash = await _connection.ExecuteScalarAsync<byte[]>(
                        new CommandDefinition(
                            "SELECT content_hash FROM snap_files WHERE snap_hash = @sh AND path = @path",
                            new { sh = snapHash, path = relPath }, cancellationToken: ct));

                    if (fileHash is null)
                        throw new DxException(DxError.SnapNotFound, $"File {relPath} not found in snap {snapHandle}");

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var store = new SqliteContentStore(_connection);
                    using var src = store.OpenRead(fileHash);
                    using var dst = File.OpenWrite(path);
                    await src.CopyToAsync(dst, ct);
                    dst.SetLength(dst.Position);
                    _logger.Debug($"  restore {relPath} from {snapHandle}");
                    break;
                }
            default:
                throw new DxException(DxError.ParseError, $"Unknown FS op: {fs.Op}");
        }
    }

    private async Task<(int ExitCode, string Output)> ExecuteRunAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        var (shell, args) = GetShell(command);

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            WorkingDirectory = _workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;

        using var cts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        var stdout = proc.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = proc.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            if (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return (124, $"Command timed out after {timeoutSeconds}s\n");
            }
            throw;
        }

        return (proc.ExitCode, (await stdout) + (await stderr));
    }

    private static (string Shell, string Args) GetShell(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/c {command}");
        return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
    }

    /// <summary>
    /// Writes the normalized audit entry to the <c>session_log</c> table.
    /// </summary>
    /// <remarks>
    /// PR 42 Invariant: This is the only method permitted to write to <c>session_log</c> 
    /// for non-genesis executions.
    /// </remarks>
    private async Task AppendLogAsync(
            SqliteTransaction? tx,
            DxDocument doc,
            string? snapHandle,
            bool success,
            CancellationToken ct)
    {
        const string sql = """
             INSERT INTO session_log (session_id, direction, document, snap_handle, tx_success, created_at)
             VALUES (@sid, @dir, @doc, @handle, @ok, @t)
             """;

        var direction = doc.Header.Author?.ToLowerInvariant() switch
        {
            "tool" => "tool",
            "llm" => "llm",
            _ => "llm"
        };

        var parameters = new
        {
            sid = _sessionId,
            dir = direction,
            doc = doc.Header.Title ?? "(untitled execution)",
            handle = snapHandle,
            ok = success ? 1 : 0,
            t = DxDatabase.UtcNow()
        };

        if (tx != null)
        {
            await _connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction: tx, cancellationToken: ct));
        }
        else
        {
            await _connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        }
    }

    private async Task<byte[]> GetCurrentHeadAsync(CancellationToken ct)
    {
        return await _connection.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = _sessionId }, cancellationToken: ct))
            ?? throw new DxException(DxError.SessionNotFound, $"Session has no HEAD: {_sessionId}");
    }

    private string ResolveAndValidate(string relOrAbs)
    {
        var abs = Path.IsPathRooted(relOrAbs)
            ? relOrAbs
            : Path.GetFullPath(Path.Combine(_workspaceRoot, relOrAbs));

        var norm = DxPath.Normalize(_workspaceRoot, abs);
        if (!DxPath.IsUnderRoot(norm))
            throw new DxException(DxError.PathEscapesRoot, $"Path escapes root: {relOrAbs}");

        return abs;
    }

    private static bool IsMutating(DxBlock block) => block.IsMutating;

    private static Encoding ResolveEncoding(string enc) => enc.ToLowerInvariant() switch
    {
        "utf-8" or "utf-8-no-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "utf-16-le" => Encoding.Unicode,
        "utf-16-be" => Encoding.BigEndianUnicode,
        "ascii" => Encoding.ASCII,
        "latin-1" => Encoding.Latin1,
        _ => new UTF8Encoding(false),
    };

    private static (Encoding Enc, bool AddBom) ResolveEncodingWithBom(string enc) => enc.ToLowerInvariant() switch
    {
        "utf-8" => (new UTF8Encoding(false), false),
        "utf-8-no-bom" => (new UTF8Encoding(false), false),
        "utf-8-bom" => (new UTF8Encoding(false), true),
        "utf-16-le" => (Encoding.Unicode, true),
        "utf-16-be" => (Encoding.BigEndianUnicode, true),
        "ascii" => (Encoding.ASCII, false),
        "latin-1" => (Encoding.Latin1, false),
        _ => (new UTF8Encoding(false), false),
    };

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return new UTF8Encoding(false);
    }

    private static byte[] StripBom(byte[] bytes, Encoding enc)
    {
        var preamble = enc.GetPreamble();
        if (preamble.Length == 0) return bytes;
        if (bytes.AsSpan().StartsWith(preamble))
            return bytes[preamble.Length..];

        return bytes;
    }

    private static string NormalizeLineEndings(string text, string style) => style switch
    {
        "lf" => text.ReplaceLineEndings("\n"),
        "crlf" => text.ReplaceLineEndings("\r\n"),
        "cr" => text.ReplaceLineEndings("\r"),
        _ => text,
    };
}
