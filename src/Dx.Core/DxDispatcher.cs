using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Dx.Core.Protocol;

public sealed record OperationResult(
    string BlockType,
    string? Path,
    bool    Success,
    string? Detail
);

public sealed record DispatchResult(
    bool   Success,
    string? NewHandle,
    string? Error,
    IReadOnlyList<OperationResult> Operations
);

/// <summary>
/// Executes a DxDocument against the working tree and database.
/// Owns the full transaction lifecycle: lock → validate → pre-snap → execute → commit/rollback.
/// </summary>
public sealed class DxDispatcher(
    SqliteConnection conn,
    string           root,
    IgnoreSet        ignoreSet,
    string           sessionId,
    IDxLogger?       logger = null)
{
    private readonly IDxLogger _log = logger ?? NullDxLogger.Instance;

    public async Task<DispatchResult> DispatchAsync(
        DxDocument doc,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var ops = new List<OperationResult>();

        // ── Read-only shortcut ────────────────────────────────────────────
        if (!doc.IsMutating)
        {
            _log.Debug("Document is read-only — dispatching requests only.");
            foreach (var block in doc.Blocks)
                await DispatchReadOnlyBlock(block, ops, ct);

            return new DispatchResult(true, null, null, ops);
        }

        // ── Mutating document: full transaction lifecycle ──────────────────
        using var dxLock = DxLock.Acquire(root);

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
                throw new DxException(DxError.BaseMismatch,
                    $"Base mismatch. Expected: {baseHandle}, Actual: {actual}");
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
            // Execute mutations (FILE, PATCH, FS) — all before any RUN
            var mutationBlocks = doc.Blocks.Where(IsMutation).ToList();
            var runBlocks      = doc.Blocks.OfType<RequestBlock>()
                                           .Where(r => r.Type == "run").ToList();

            foreach (var block in mutationBlocks)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Applying {block.GetType().Name}...");
                await DispatchMutationBlock(block, ops, ct);
            }

            // Run blocks act as gates
            foreach (var run in runBlocks)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Running: {run.Body.Trim()[..Math.Min(40, run.Body.Trim().Length)]}...");
                var (exitCode, output) = await ExecuteRunAsync(run.Body.Trim(), ct);

                ops.Add(new("REQUEST:run", null, exitCode == 0,
                    $"exit={exitCode}\n{output}"));

                if (exitCode != 0)
                    throw new DxException(DxError.InvalidArgument,
                        $"Run gate failed with exit code {exitCode}:\n{output}");
            }

            // Commit: build snap
            progress?.Report("Snapshotting...");
            var manifest = ManifestBuilder.Build(root, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            // No-op check
            if (DxHash.Equal(snapHash, currentHead))
            {
                ClearPending();
                var existingHandle = HandleAssigner.ReverseResolve(conn, sessionId, currentHead)!;
                return new DispatchResult(true, existingHandle, null, ops);
            }

            var writer    = new SnapshotWriter(conn);
            var newHandle = writer.Persist(sessionId, snapHash, manifest);

            // Log to session_log
            AppendLog(doc, newHandle, success: true);

            ClearPending();
            _log.Info($"→ {newHandle}");

            return new DispatchResult(true, newHandle, null, ops);
        }
        catch (Exception ex)
        {
            _log.Error($"Transaction failed: {ex.Message}");

            // Rollback working tree
            var engine = new RollbackEngine(conn, root, ignoreSet);
            engine.RestoreTo(currentHead);

            AppendLog(doc, null, success: false);
            ClearPending();

            var err = ex is DxException dxEx ? dxEx.Message : ex.Message;
            return new DispatchResult(false, null, err, ops);
        }
    }

    // ── Mutation dispatch ─────────────────────────────────────────────────

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
                ops.Add(new("PATCH", pb.Path, true,
                    $"{pb.Hunks.Count} hunk(s)"));
                break;

            case FsBlock fs:
                ExecuteFsOp(fs);
                ops.Add(new($"FS:{fs.Op}", fs.Args.GetValueOrDefault("path"), true, null));
                break;
        }

        await Task.CompletedTask;
    }

    // ── Read-only dispatch ────────────────────────────────────────────────

    private async Task DispatchReadOnlyBlock(
        DxBlock block, List<OperationResult> ops, CancellationToken ct)
    {
        if (block is not RequestBlock req) return;

        switch (req.Type)
        {
            case "run":
                var (exit, output) = await ExecuteRunAsync(req.Body.Trim(), ct);
                ops.Add(new("REQUEST:run", null, exit == 0, output));
                break;

            // Other REQUEST types (file, tree, search) are informational —
            // their results are surfaced in CLI renderers, not here.
            default:
                ops.Add(new($"REQUEST:{req.Type}", null, true, null));
                break;
        }
    }

    // ── FILE write ────────────────────────────────────────────────────────

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

    // ── PATCH apply ───────────────────────────────────────────────────────

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

    // ── FS operations ─────────────────────────────────────────────────────

    private void ExecuteFsOp(FsBlock fs)
    {
        switch (fs.Op)
        {
            case "move":
            {
                var from = ResolveAndValidate(fs.Args.GetValueOrDefault("from", ""));
                var to   = ResolveAndValidate(fs.Args.GetValueOrDefault("to", ""));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, overwrite: true);
                _log.Debug($"  move {fs.Args["from"]} → {fs.Args["to"]}");
                break;
            }
            case "delete":
            {
                var path      = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                var recursive = fs.Args.GetValueOrDefault("recursive", "false") == "true";
                var ifExists  = fs.Args.GetValueOrDefault("if-exists", "false") == "true";

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
                var path        = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                var toEncoding  = fs.Args.GetValueOrDefault("to", "utf-8");
                var lineEndings = fs.Args.GetValueOrDefault("line-endings", "preserve");

                var bytes   = File.ReadAllBytes(path);
                var srcEnc  = DetectEncoding(bytes);
                var text    = srcEnc.GetString(StripBom(bytes, srcEnc));

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
                var path       = ResolveAndValidate(fs.Args.GetValueOrDefault("path", ""));
                var snapHandle = fs.Args.GetValueOrDefault("snap", "")
                    ?? throw new DxException(DxError.InvalidArgument,
                        "restore requires snap= argument");

                var snapHash = HandleAssigner.Resolve(conn, sessionId, snapHandle)
                    ?? throw new DxException(DxError.SnapNotFound,
                        $"Snap not found: {snapHandle}");

                var relPath  = DxPath.Normalize(root, path);
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
                using var src = BlobStore.OpenRead(conn, fileHash);
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

    // ── Run execution ─────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)>
        ExecuteRunAsync(string command, CancellationToken ct)
    {
        var (shell, args) = GetShell(command);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = shell,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc   = System.Diagnostics.Process.Start(psi)!;
        var stdout       = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr       = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var output = (await stdout) + (await stderr);
        return (proc.ExitCode, output);
    }

    private static (string Shell, string Args) GetShell(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/c {command}");
        return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
    }

    // ── Pending transaction guard ─────────────────────────────────────────

    private void BeginPending(byte[] headHash)
        => conn.Execute(
            """
            INSERT INTO pending_transaction
                (id, session_id, target_snap_hash, started_utc)
            VALUES (1, @sid, @hash, @t)
            """,
            new { sid = sessionId, hash = headHash, t = DxDatabase.UtcNow() });

    private void ClearPending()
        => conn.Execute("DELETE FROM pending_transaction WHERE id = 1");

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

    // ── Session log ───────────────────────────────────────────────────────

    private void AppendLog(DxDocument doc, string? snapHandle, bool success)
        => conn.Execute(
            """
            INSERT INTO session_log
                (session_id, direction, document, snap_handle, tx_success, created_at)
            VALUES (@sid, @dir, @doc, @handle, @ok, @t)
            """,
            new
            {
                sid    = sessionId,
                dir    = doc.Header.Author ?? "llm",
                doc    = "(document)",      // full text stored by CLI layer
                handle = snapHandle,
                ok     = success ? 1 : 0,
                t      = DxDatabase.UtcNow()
            });

    // ── Helpers ────────────────────────────────────────────────────────────

    private byte[] GetCurrentHead()
        => conn.ExecuteScalar<byte[]>(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = sessionId })
           ?? throw new DxException(DxError.SessionNotFound,
               $"Session has no HEAD: {sessionId}");

    private string ResolveAndValidate(string relOrAbs)
    {
        var abs  = Path.IsPathRooted(relOrAbs)
            ? relOrAbs
            : Path.GetFullPath(Path.Combine(root, relOrAbs));

        var norm = DxPath.Normalize(root, abs);
        if (!DxPath.IsUnderRoot(norm))
            throw new DxException(DxError.PathEscapesRoot,
                $"Path escapes root: {relOrAbs}");

        return abs;
    }

    private static bool IsMutation(DxBlock b) => b switch
    {
        FileBlock f  => !f.ReadOnly,
        PatchBlock   => true,
        FsBlock      => true,
        _            => false,
    };

    private static Encoding ResolveEncoding(string enc) => enc.ToLowerInvariant() switch
    {
        "utf-8" or "utf-8-no-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "utf-8-bom"               => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "utf-16-le"               => Encoding.Unicode,
        "utf-16-be"               => Encoding.BigEndianUnicode,
        "ascii"                   => Encoding.ASCII,
        "latin-1"                 => Encoding.Latin1,
        _                         => new UTF8Encoding(false),
    };

    private static (Encoding Enc, bool AddBom) ResolveEncodingWithBom(string enc)
        => enc.ToLowerInvariant() switch
        {
            "utf-8"        => (new UTF8Encoding(false), false),
            "utf-8-no-bom" => (new UTF8Encoding(false), false),
            "utf-8-bom"    => (new UTF8Encoding(false), true),
            "utf-16-le"    => (Encoding.Unicode, true),
            "utf-16-be"    => (Encoding.BigEndianUnicode, true),
            "ascii"        => (Encoding.ASCII, false),
            "latin-1"      => (Encoding.Latin1, false),
            _              => (new UTF8Encoding(false), false),
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
        "lf"   => text.ReplaceLineEndings("\n"),
        "crlf" => text.ReplaceLineEndings("\r\n"),
        "cr"   => text.ReplaceLineEndings("\r"),
        _      => text,
    };
}
