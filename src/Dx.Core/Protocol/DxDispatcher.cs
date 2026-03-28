using System.Text;
using System.Text.Json;

using Dapper;

using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

namespace Dx.Core.Protocol;

/// <summary>
/// Executes a <see cref="DxDocument"/> against the workspace working tree and database,
/// owning the full transaction lifecycle: acquire lock → crash recovery → base check →
/// execute mutations → run gates → snapshot → commit or rollback.
/// </summary>
/// <param name="conn">An open database connection to the workspace <c>snap.db</c>.</param>
/// <param name="root">The absolute workspace root path.</param>
/// <param name="ignoreSet">The file exclusion rules for the active session.</param>
/// <param name="sessionId">The identifier of the session being transacted against.</param>
/// <param name="logger">An optional diagnostic logger; defaults to <see cref="NullDxLogger"/>.</param>
public sealed class DxDispatcher(
    SqliteConnection conn,
    string root,
    IgnoreSet ignoreSet,
    string sessionId,
    IDxLogger? logger = null)
{
    private readonly IDxLogger _log = logger ?? NullDxLogger.Instance;

    /// <summary>
    /// Dispatches a parsed <see cref="DxDocument"/>, applying its blocks to the workspace
    /// according to the full transactional protocol.
    /// </summary>
    /// <param name="doc">The document to dispatch.</param>
    /// <param name="dryRun">
    /// When <see langword="true"/>, the base check is performed and mutations are
    /// validated but no changes are written and no snapshot is created.
    /// </param>
    /// <param name="progress">
    /// An optional progress sink that receives human-readable status strings as each
    /// block is processed.
    /// </param>
    /// <param name="options">
    /// Optional per-invocation overrides for base-mismatch behaviour and run timeout.
    /// When <see langword="null"/>, workspace configuration defaults apply.
    /// </param>
    /// <param name="ct">A cancellation token that can interrupt the operation.</param>
    /// <returns>
    /// A <see cref="DispatchResult"/> describing whether the transaction succeeded, the
    /// new snapshot handle (if any), any error message, the per-block operation log, and
    /// whether the failure was a base-mismatch.
    /// </returns>
    public async Task<DispatchResult> DispatchAsync(
        DxDocument doc,
        bool dryRun = false,
        IProgress<string>? progress = null,
        ApplyOptions? options = null,
        CancellationToken ct = default)
    {
        var ops = new List<OperationResult>();

        // ── Read-only shortcut ────────────────────────────────────────────────
        if (!doc.IsMutating)
        {
            _log.Debug("Document is read-only — dispatching requests only.");
            foreach (var block in doc.Blocks)
                await DispatchReadOnlyBlock(block, ops, options, ct);

            return new DispatchResult(true, null, null, ops);
        }

        // ── Mutating document: full transaction lifecycle ──────────────────────
        var lockFile = Path.Combine(root, ".dx", "snaps.lock");
        await using var dxLock =await DxLock.AcquireAsync(lockFile, TimeSpan.FromSeconds(5), ct);

        // Crash recovery
        RecoverIfNeeded();

        // Base check
        var currentHead = GetCurrentHead();
        if (doc.Header.Base is { } baseHandle)
        {
            var baseHash = HandleAssigner.Resolve(conn, sessionId, baseHandle)
                ?? throw new DxException(DxError.SnapNotFound,
                    $"Base handle not found: {baseHandle}");

            if (!DxHash.Equal(baseHash, currentHead))
            {
                var actual = HandleAssigner.ReverseResolve(conn, sessionId, currentHead) ?? "?";
                var mismatchMsg = $"Base mismatch. Expected: {baseHandle}, Actual: {actual}";

                // --on-base-mismatch warn: log and continue rather than aborting
                var mismatchBehaviour = options?.OnBaseMismatch?.ToLowerInvariant() ?? "reject";
                if (mismatchBehaviour == "warn")
                {
                    _log.Warn(mismatchMsg);
                }
                else
                {
                try { AppendLog(doc, null, success: false); } catch { /* best-effort */ }
                    return new DispatchResult(
                        Success: false,
                        NewHandle: null,
                        Error: mismatchMsg,
                        Operations: ops,
                        IsBaseMismatch: true);
                }
            }
        }

        if (dryRun)
        {
            _log.Info("Dry run — no changes applied.");
            return new DispatchResult(true, null, null, ops);
        }

        // Begin pending transaction guard
        BeginPending(currentHead);

        try
        {
            // Execute mutations (FILE, PATCH, FS) — all before any RUN gates
            var mutationBlocks = doc.Blocks.Where(IsMutation).ToList();
            var runBlocks = doc.Blocks.OfType<RequestBlock>()
                                           .Where(r => r.Type == "run").ToList();

            foreach (var block in mutationBlocks)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Applying {block.GetType().Name}...");
                await DispatchMutationBlock(block, ops, ct);
            }

            // Run blocks act as gates: a non-zero exit code aborts the transaction.
            // Per-invocation RunTimeoutSeconds overrides the config default.
            var runTimeout = options?.RunTimeoutSeconds ?? 0;
            foreach (var run in runBlocks)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Running: {run.Body.Trim()[..Math.Min(40, run.Body.Trim().Length)]}...");
                var (exitCode, output) = await ExecuteRunAsync(run.Body.Trim(), runTimeout, ct);

                ops.Add(new("REQUEST:run", null, exitCode == 0,
                    $"exit={exitCode}\n{output}"));

                if (exitCode != 0)
                    throw new DxException(DxError.InvalidArgument,
                        $"Run gate failed with exit code {exitCode}:\n{output}");
            }

            // Commit: build snap from the current working tree
            progress?.Report("Snapshotting...");
            var manifest = ManifestBuilder.Build(root, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            // No-op check: if the tree is unchanged, reuse the existing handle
            if (DxHash.Equal(snapHash, currentHead))
            {
                ClearPending();
                var existingHandle = HandleAssigner.ReverseResolve(conn, sessionId, currentHead)!;
                return new DispatchResult(true, existingHandle, null, ops);
            }

            var writer = new SnapshotWriter(conn);
            var newHandle = await writer.PersistAsync(sessionId, snapHash, manifest);

            AppendLog(doc, newHandle, success: true);
            ClearPending();
            _log.Info($"→ {newHandle}");

            return new DispatchResult(true, newHandle, null, ops);
        }
        catch (Exception ex)
        {
            _log.Error($"Transaction failed: {ex.Message}");

            // Rollback working tree to the pre-transaction state
            var engine = new RollbackEngine(conn, root, ignoreSet);
            engine.RestoreTo(currentHead);

            AppendLog(doc, null, success: false);
            ClearPending();

            var err = ex is DxException dxEx ? dxEx.Message : ex.Message;
            return new DispatchResult(false, null, err, ops);
        }
    }

    // ── Mutation dispatch ─────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a single mutation block (<see cref="FileBlock"/>,
    /// <see cref="PatchBlock"/>, or <see cref="FsBlock"/>) to the appropriate handler
    /// and records the result.
    /// </summary>
    private async Task DispatchMutationBlock(
        DxBlock block, List<OperationResult> ops, CancellationToken ct)
    {
        switch (block)
        {
            case FileBlock fb when !fb.ReadOnly:
                WriteFile(fb);
                ops.Add(new("FILE", fb.Path, true, null));
                break;

            case PatchBlock pb:
                ApplyPatch(pb);
                ops.Add(new("PATCH", pb.Path, true, $"{pb.Hunks.Count} hunk(s)"));
                break;

            case FsBlock fs:
                ExecuteFsOp(fs);
                ops.Add(new($"FS:{fs.Op}", fs.Args.GetValueOrDefault("path"), true, null));
                break;
        }

        await Task.CompletedTask;
    }

    // ── Read-only dispatch ────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a read-only block. Only <see cref="RequestBlock"/> entries with
    /// <c>type=run</c> result in actual execution; all other request types are
    /// acknowledged as informational.
    /// </summary>
    private async Task DispatchReadOnlyBlock(
        DxBlock block, List<OperationResult> ops, ApplyOptions? options,
        CancellationToken ct)
    {
        if (block is not RequestBlock req) return;

        switch (req.Type)
        {
            case "run":
                var runTimeout = options?.RunTimeoutSeconds ?? 0;
                var (exit, output) = await ExecuteRunAsync(req.Body.Trim(), runTimeout, ct);
                ops.Add(new("REQUEST:run", null, exit == 0, output));
                break;

            // Other REQUEST types (file, tree, search) are informational —
            // their results are surfaced in CLI renderers, not here.
            default:
                ops.Add(new($"REQUEST:{req.Type}", null, true, null));
                break;
        }
    }

    // ── FILE write ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the content of a <see cref="FileBlock"/> to the workspace, honouring
    /// <c>create</c>, <c>if-contains</c>, and <c>if-line</c> preconditions.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.InvalidArgument"/> when a precondition fails or
    /// <c>create=false</c> and the file does not exist.
    /// </exception>
    private void WriteFile(FileBlock fb)
    {
        var absPath = ResolveAndValidate(fb.Path);

        if (!fb.Create && !File.Exists(absPath))
            throw new DxException(DxError.InvalidArgument,
                $"File does not exist and create=false: {fb.Path}");

        if (fb.IfContains is { } ic && File.Exists(absPath))
            if (!File.ReadAllText(absPath).Contains(ic, StringComparison.Ordinal))
                throw new DxException(DxError.InvalidArgument,
                    $"Precondition failed (if-contains): \"{ic}\" not found in {fb.Path}");

        if (fb.IfLine is { } il)
        {
            var parts = il.Split(':', 2);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var lineNo)
                && File.Exists(absPath))
            {
                var lines = File.ReadAllLines(absPath);
                if (lineNo < 1 || lineNo > lines.Length
                    || !lines[lineNo - 1].Contains(parts[1], StringComparison.Ordinal))
                    throw new DxException(DxError.InvalidArgument,
                        $"Precondition failed (if-line): line {lineNo} does not match \"{parts[1]}\"");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

        var encoding = ResolveEncoding(fb.Encoding);
        File.WriteAllText(absPath, fb.Content, encoding);

        _log.Debug($"  write {fb.Path}");
    }

    // ── PATCH apply ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies all hunks in a <see cref="PatchBlock"/> to an existing workspace file.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.InvalidArgument"/> when the target file does not
    /// exist.
    /// </exception>
    private void ApplyPatch(PatchBlock pb)
    {
        var absPath = ResolveAndValidate(pb.Path);

        if (!File.Exists(absPath))
            throw new DxException(DxError.InvalidArgument,
                $"Cannot patch non-existent file: {pb.Path}");

        var content = File.ReadAllText(absPath);
        var patched = PatchEngine.Apply(content, pb.Hunks);
        File.WriteAllText(absPath, patched);

        _log.Debug($"  patch {pb.Path} ({pb.Hunks.Count} hunks)");
    }

    // ── FS operations ─────────────────────────────────────────────────────────

    /// <summary>
    /// Executes the filesystem operation described by an <see cref="FsBlock"/>.
    /// Supported operations: <c>move</c>, <c>delete</c>, <c>encode</c>,
    /// <c>checkout</c>, <c>restore</c>.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> for unknown operations, or with
    /// <see cref="DxError.InvalidArgument"/> / <see cref="DxError.SnapNotFound"/> for
    /// argument and precondition failures within a recognised operation.
    /// </exception>
    private void ExecuteFsOp(FsBlock fs)
    {
        switch (fs.Op)
        {
            case "move":
                {
                    var from = ResolveAndValidate(fs.Args.GetValueOrDefault("from", ""));
                    var to = ResolveAndValidate(fs.Args.GetValueOrDefault("to", ""));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                    _log.Debug($"  move {fs.Args["from"]} → {fs.Args["to"]}");
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
                            throw new DxException(DxError.InvalidArgument,
                                $"Cannot delete directory without recursive=true: {fs.Args["path"]}");
                        Directory.Delete(path, recursive: true);
                    }
                    else if (File.Exists(path))
                        File.Delete(path);
                    else if (!ifExists)
                        throw new DxException(DxError.InvalidArgument,
                            $"Path not found: {fs.Args["path"]}");

                    _log.Debug($"  delete {fs.Args["path"]}");
                    break;
                }
            case "encode":
                {
                    var path = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                    var toEncoding = fs.Args.GetValueOrDefault("to", "utf-8");
                    var lineEndings = fs.Args.GetValueOrDefault("line-endings", "preserve");

                    var bytes = File.ReadAllBytes(path);
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
                        fs2.Write(bom);
                    }
                    fs2.Write(outBytes);
                    fs2.SetLength(fs2.Position);

                    _log.Debug($"  encode {fs.Args["path"]} → {toEncoding}");
                    break;
                }
            case "checkout":
                {
                    var snapHandle = fs.Args.GetValueOrDefault("snap", "")
                        ?? throw new DxException(DxError.InvalidArgument,
                            "checkout requires snap= argument");

                    var snapHash = HandleAssigner.Resolve(conn, sessionId, snapHandle)
                        ?? throw new DxException(DxError.SnapNotFound,
                            $"Snap not found: {snapHandle}");

                    var engine = new RollbackEngine(conn, root, ignoreSet);
                    engine.RestoreTo(snapHash);
                    _log.Debug($"  checkout {snapHandle}");
                    break;
                }
            case "restore":
                {
                    var path = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                    var snapHandle = fs.Args.GetValueOrDefault("snap", "")
                        ?? throw new DxException(DxError.InvalidArgument,
                            "restore requires snap= argument");

                    var snapHash = HandleAssigner.Resolve(conn, sessionId, snapHandle)
                        ?? throw new DxException(DxError.SnapNotFound,
                            $"Snap not found: {snapHandle}");

                    var relPath = DxPath.Normalize(root, path);
                    var fileHash = conn.ExecuteScalar<byte[]>(
                        """
                    SELECT sf.content_hash
                    FROM snap_files sf
                    WHERE sf.snap_hash = @sh AND sf.path = @path
                    """,
                        new { sh = snapHash, path = relPath });

                    if (fileHash is null)
                        throw new DxException(DxError.SnapNotFound,
                            $"File {relPath} not found in snap {snapHandle}");

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var store = new SqliteContentStore(conn);
                    using var src = store.OpenRead(fileHash);
                    using var dst = File.OpenWrite(path);
                    src.CopyTo(dst);
                    dst.SetLength(dst.Position);
                    _log.Debug($"  restore {relPath} from {snapHandle}");
                    break;
                }
            default:
                throw new DxException(DxError.ParseError, $"Unknown FS op: {fs.Op}");
        }
    }

    // ── Run execution ─────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a shell command and returns its exit code and combined output.
    /// The command is run in the workspace root directory.
    /// </summary>
    /// <param name="command">The shell command string to execute.</param>
    /// <param name="timeoutSeconds">
    /// Timeout in seconds; <c>0</c> means no timeout. Overrides workspace config when
    /// supplied by the caller via <see cref="ApplyOptions.RunTimeoutSeconds"/>.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    private static async Task<(int ExitCode, string Output)>
        ExecuteRunAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        var (shell, args) = GetShell(command);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        if (timeoutSeconds > 0)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                proc.Kill(entireProcessTree: true);
                return (124, $"Command timed out after {timeoutSeconds}s\n");
            }
        }
        else
        {
            await proc.WaitForExitAsync(ct);
        }

        var output = (await stdout) + (await stderr);
        return (proc.ExitCode, output);
    }

    /// <summary>
    /// Returns the shell executable and argument string appropriate for the current OS.
    /// </summary>
    private static (string Shell, string Args) GetShell(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/c {command}");
        return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
    }

    // ── Pending transaction guard ─────────────────────────────────────────────

    /// <summary>
    /// Records the start of a pending transaction in the database so that a subsequent
    /// crash can be detected and the working tree rolled back during recovery.
    /// Uses <c>INSERT OR REPLACE</c> so that a stale row left by a prior crash is safely
    /// overwritten rather than causing a unique-constraint violation that would permanently
    /// lock the workspace.
    /// </summary>
    private void BeginPending(byte[] headHash)
        => conn.Execute(
            """
            INSERT OR REPLACE INTO pending_transaction
                (id, session_id, target_snap_hash, started_utc)
            VALUES (1, @sid, @hash, @t)
            """,
            new { sid = sessionId, hash = headHash, t = DxDatabase.UtcNow() });

    /// <summary>
    /// Removes the pending transaction record once a transaction has committed or been
    /// explicitly rolled back.
    /// </summary>
    private void ClearPending()
        => conn.Execute("DELETE FROM pending_transaction WHERE id = 1");

    /// <summary>
    /// Checks for a leftover pending transaction record from a previous crash and, if
    /// one exists for the current session, rolls the working tree back to the pre-crash
    /// state.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.PendingTransactionOnOtherSession"/> when the
    /// pending record belongs to a different session, which requires manual intervention.
    /// </exception>
    private void RecoverIfNeeded()
    {
        var row = conn.QuerySingleOrDefault<(string SessionId, byte[] TargetHash)>(
            "SELECT session_id, target_snap_hash FROM pending_transaction WHERE id = 1");

        if (row == default) return;

        if (row.SessionId != sessionId)
            throw new DxException(DxError.PendingTransactionOnOtherSession,
                $"Pending transaction on session {row.SessionId}.");

        if (row.TargetHash is not null)
        {
            _log.Warn("Crash recovery: restoring to last known good snap.");
            new RollbackEngine(conn, root, ignoreSet).RestoreTo(row.TargetHash);
        }

        ClearPending();
    }

    // ── Session log ───────────────────────────────────────────────────────────

    /// <summary>
    /// Appends a transaction record to the session log.
    /// </summary>
    /// <param name="doc">The document that was dispatched.</param>
    /// <param name="snapHandle">
    /// The resulting snapshot handle, or <see langword="null"/> when the transaction
    /// failed.
    /// </param>
    /// <param name="success">
    /// <see langword="true"/> when the transaction committed;
    /// <see langword="false"/> otherwise.
    /// </param>
    /// <remarks>
    /// The <c>direction</c> column has a <c>CHECK (direction IN ('llm','tool'))</c>
    /// constraint. The author value from the document header is normalised here so that
    /// any unexpected value (e.g. <c>robot</c>, <c>null</c>) defaults to <c>llm</c>
    /// rather than crashing with a SQLite constraint violation after mutations have
    /// already been applied.
    /// </remarks>
    private void AppendLog(DxDocument doc, string? snapHandle, bool success)
        => conn.Execute(
            """
            INSERT INTO session_log
                (session_id, direction, document, snap_handle, tx_success, created_at)
            VALUES (@sid, @dir, @doc, @handle, @ok, @t)
            """,
            new
            {
                sid = sessionId,
                dir = doc.Header.Author?.ToLowerInvariant() == "tool" ? "tool" : "llm",
                doc = "(document)",
                handle = snapHandle,
                ok = success ? 1 : 0,
                t = DxDatabase.UtcNow()
            });

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Returns the raw SHA-256 hash of the current HEAD snapshot.</summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.SessionNotFound"/> when the session has no HEAD.
    /// </exception>
    private byte[] GetCurrentHead()
        => conn.ExecuteScalar<byte[]>(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = sessionId })
           ?? throw new DxException(DxError.SessionNotFound,
               $"Session has no HEAD: {sessionId}");

    /// <summary>
    /// Resolves a relative or absolute path to an absolute OS path, validating that it
    /// does not escape the workspace root.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.PathEscapesRoot"/> when the resolved path is
    /// outside the workspace root.
    /// </exception>
    private string ResolveAndValidate(string relOrAbs)
    {
        var abs = Path.IsPathRooted(relOrAbs)
            ? relOrAbs
            : Path.GetFullPath(Path.Combine(root, relOrAbs));

        var norm = DxPath.Normalize(root, abs);
        if (!DxPath.IsUnderRoot(norm))
            throw new DxException(DxError.PathEscapesRoot,
                $"Path escapes root: {relOrAbs}");

        return abs;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the block is a mutating operation that should
    /// be executed before any run gates.
    /// </summary>
    private static bool IsMutation(DxBlock b) => b switch
    {
        FileBlock f => !f.ReadOnly,
        PatchBlock => true,
        FsBlock => true,
        _ => false,
    };

    /// <summary>Resolves an encoding name to the corresponding <see cref="Encoding"/>.</summary>
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

    /// <summary>
    /// Resolves an encoding name to an <see cref="Encoding"/> and a flag indicating
    /// whether a BOM preamble should be written before the encoded bytes.
    /// </summary>
    private static (Encoding Enc, bool AddBom) ResolveEncodingWithBom(string enc)
        => enc.ToLowerInvariant() switch
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

    /// <summary>
    /// Detects the encoding of a byte array by inspecting its BOM preamble.
    /// Defaults to UTF-8 without BOM when no preamble is found.
    /// </summary>
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

    /// <summary>
    /// Strips the BOM preamble from a byte array if the detected encoding has one.
    /// Returns the original array unchanged when no BOM is present.
    /// </summary>
    private static byte[] StripBom(byte[] bytes, Encoding enc)
    {
        var preamble = enc.GetPreamble();
        if (preamble.Length == 0) return bytes;
        if (bytes.AsSpan().StartsWith(preamble))
            return bytes[preamble.Length..];
        return bytes;
    }

    /// <summary>Normalises all line endings in a string to the specified style.</summary>
    private static string NormalizeLineEndings(string text, string style) => style switch
    {
        "lf" => text.ReplaceLineEndings("\n"),
        "crlf" => text.ReplaceLineEndings("\r\n"),
        "cr" => text.ReplaceLineEndings("\r"),
        _ => text,
    };
}




