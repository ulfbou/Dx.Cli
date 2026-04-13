using Dapper;

using Dx.Core.Execution;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

using System.Text;

namespace Dx.Core.Protocol;

/// <summary>
/// Executes a <see cref="DxDocument"/> against the workspace working tree and database,
/// orchestrating the application of individual blocks and verifying run-gates.
/// </summary>
/// <remarks>
/// This class enforces the transactional integrity of the DX protocol by delegating 
/// all mutating operations to the <see cref="TransactionCoordinator"/>. It ensures 
/// that no filesystem changes occur without crash recovery protection and 
/// atomicity guarantees.
/// </remarks>
public sealed class DxDispatcher
{
    private readonly IDxLogger _logger;
    private readonly SqliteConnection _connection;
    private readonly string _workspaceRoot;
    private readonly IgnoreSet _ignoreSet;
    private readonly string _sessionId;

    /// <summary>
    /// Initializes a new instance of the <see cref="DxDispatcher"/> class.
    /// </summary>
    /// <param name="connection">An open database connection to the workspace <c>snap.db</c>.</param>
    /// <param name="workspaceRoot">The absolute workspace root path.</param>
    /// <param name="ignoreSet">The file exclusion rules for the active session.</param>
    /// <param name="sessionId">The identifier of the session being transacted against.</param>
    /// <param name="logger">An optional diagnostic logger; defaults to <see cref="NullDxLogger"/>.</param>
    public DxDispatcher(
        SqliteConnection connection,
        string workspaceRoot,
        IgnoreSet ignoreSet,
        string sessionId,
        IDxLogger? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _ignoreSet = ignoreSet ?? throw new ArgumentNullException(nameof(ignoreSet));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _logger = logger ?? NullDxLogger.Instance;
    }

    /// <summary>
    /// Dispatches a parsed <see cref="DxDocument"/> according to the transactional protocol.
    /// </summary>
    /// <param name="document">The document to dispatch.</param>
    /// <param name="dryRun">
    /// When <see langword="true"/>, the operation is validated and run-gates are 
    /// checked (where possible), but no changes are committed to disk or database.
    /// </param>
    /// <param name="progress">An optional progress sink for real-time status updates.</param>
    /// <param name="options">In-flight overrides for timeout and base-mismatch behavior.</param>
    /// <param name="ct">A cancellation token to interrupt execution.</param>
    /// <returns>A <see cref="DispatchResult"/> describing the success and operations performed.</returns>
    public async Task<DispatchResult> DispatchAsync(
        DxDocument document,
        bool dryRun = false,
        IProgress<string>? progress = null,
        ApplyOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.IsMutating)
        {
            _logger.Debug("Document is read-only — dispatching requests only.");
            var roOps = new List<OperationResult>();
            foreach (var block in document.Blocks)
            {
                await DispatchReadOnlyBlockAsync(block, roOps, options, ct);
            }
            return new DispatchResult(true, null, null, roOps);
        }

        return await ExecuteMutatingTransactionAsync(document, dryRun, progress, options, ct);
    }

    /// <summary>
    /// Dispatches an execution request containing a document and context.
    /// </summary>
    /// <param name="request">The execution request.</param>
    /// <returns>A <see cref="DispatchResult"/> describing the success and operations performed.</returns>
    public Task<DispatchResult> DispatchAsync(DxExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(
            request.Document,
            dryRun: request.IsDryRun,
            progress: request.Progress,
            options: request.Options,
            ct: request.CancellationToken);
    }

    /// <summary>
    /// The authoritative choke point for all mutating operations. Enforces the 
    /// TransactionCoordinator lifecycle.
    /// </summary>
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
        var ops = new List<OperationResult>();

        try
        {
            // ENFORCEMENT: Recovery and Mutation are wrapped in a single durable authority.
            return await coordinator.RunAsync(_sessionId, async (tx, innerCt) =>
            {
                var currentHead = await GetCurrentHeadAsync(innerCt);

                // Base Validation
                if (document.Header.Base is { } baseHandle)
                {
                    var baseHash = HandleAssigner.Resolve(_connection, _sessionId, baseHandle);
                    if (baseHash == null || !DxHash.Equal(baseHash, currentHead))
                    {
                        var actual = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHead) ?? "?";
                        var mismatchMsg = $"Base mismatch. Expected: {baseHandle}, Actual: {actual}";
                        var mismatchBehaviour = options?.OnBaseMismatch?.ToLowerInvariant() ?? "reject";

                        if (mismatchBehaviour == "warn")
                        {
                            _logger.Warn(mismatchMsg);
                        }
                        else
                        {
                            await AppendLogAsync(tx, document, null, false, innerCt);
                            return new DispatchResult(false, null, mismatchMsg, ops, IsBaseMismatch: true);
                        }
                    }
                }

                if (dryRun)
                {
                    _logger.Info("Dry run — no changes applied.");
                    return new DispatchResult(true, null, null, ops);
                }

                // Execution: Apply mutations and verify gates
                var mutationBlocks = document.Blocks.Where(IsMutation).ToList();
                var runBlocks = document.Blocks.OfType<RequestBlock>().Where(r => r.Type == "run").ToList();

                foreach (var block in mutationBlocks)
                {
                    innerCt.ThrowIfCancellationRequested();
                    progress?.Report($"Applying {block.GetType().Name}...");
                    await DispatchMutationBlockAsync(block, ops, innerCt);
                }

                var runTimeout = options?.RunTimeoutSeconds ?? 0;
                foreach (var run in runBlocks)
                {
                    innerCt.ThrowIfCancellationRequested();
                    var body = run.Body.Trim();
                    progress?.Report($"Running: {body[..Math.Min(40, body.Length)]}...");

                    var (exitCode, output) = await ExecuteRunAsync(body, runTimeout, innerCt);

                    ops.Add(new OperationResult("REQUEST:run", null, exitCode == 0, $"exit={exitCode}\n{output}"));

                    if (exitCode != 0)
                    {
                        throw new DxException(DxError.InvalidArgument, $"Run gate failed with exit code {exitCode}:\n{output}");
                    }
                }

                progress?.Report("Snapshotting...");
                var manifest = ManifestBuilder.Build(_workspaceRoot, _ignoreSet);
                var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

                if (DxHash.Equal(snapHash, currentHead))
                {
                    var existingHandle = HandleAssigner.ReverseResolve(_connection, _sessionId, currentHead)!;
                    return new DispatchResult(true, existingHandle, null, ops);
                }

                var writer = new SnapshotWriter(_connection);
                var newHandle = await writer.PersistAsync(_sessionId, snapHash, manifest, innerCt, tx);

                await AppendLogAsync(tx, document, newHandle, true, innerCt);
                _logger.Info($"→ {newHandle}");

                return new DispatchResult(true, newHandle, null, ops);
            }, ct);
        }
        catch (Exception ex)
        {
            // Coordinator rolled back files/transaction. We must log the failure outside of that transaction.
            await AppendLogAsync(null, document, null, false, ct);
            var err = ex is DxException dxEx ? dxEx.Message : ex.Message;
            return new DispatchResult(false, null, err, ops);
        }
    }

    private async Task DispatchMutationBlockAsync(DxBlock block, List<OperationResult> ops, CancellationToken ct)
    {
        switch (block)
        {
            case FileBlock fb when !fb.ReadOnly:
                await WriteFileAsync(fb, ct);
                ops.Add(new OperationResult("FILE", fb.Path, true, null));
                break;
            case PatchBlock pb:
                await ApplyPatchAsync(pb, ct);
                ops.Add(new OperationResult("PATCH", pb.Path, true, $"{pb.Hunks.Count} hunk(s)"));
                break;
            case FsBlock fs:
                await ExecuteFsOpAsync(fs, ct);
                ops.Add(new OperationResult($"FS:{fs.Op}", fs.Args.GetValueOrDefault("path"), true, null));
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
                ops.Add(new OperationResult("REQUEST:run", null, exit == 0, output));
                break;
            default:
                ops.Add(new OperationResult($"REQUEST:{req.Type}", null, true, null));
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

                    await _connection.ExecuteAsync(
                        """
                        INSERT INTO session_log
                          (session_id, direction, document, snap_handle, tx_success, created_at) VALUES
                          (@sid, 'tool', '(checkout)', @handle, 1, @now)
                        """,
                        new
                        {
                            sid = _sessionId,
                            handle = snapHandle,
                            now = DxDatabase.UtcNow()
                        });

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

        var psi = new System.Diagnostics.ProcessStartInfo
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

    private async Task AppendLogAsync(SqliteTransaction? tx, DxDocument doc, string? snapHandle, bool success, CancellationToken ct)
    {
        var sql = """
            INSERT INTO session_log (session_id, direction, document, snap_handle, tx_success, created_at)
            VALUES (@sid, @dir, @doc, @handle, @ok, @t)
            """;

        var parameters = new
        {
            sid = _sessionId,
            dir = doc.Header.Author?.ToLowerInvariant() == "tool" ? "tool" : "llm",
            doc = "(document)",
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

    private static bool IsMutation(DxBlock b) => b switch
    {
        FileBlock f => !f.ReadOnly,
        PatchBlock => true,
        FsBlock => true,
        _ => false,
    };

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
