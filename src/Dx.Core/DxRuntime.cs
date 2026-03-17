using Dapper;
using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// High-level facade. CLI commands call this; they never touch DB or dispatcher directly.
/// </summary>
public sealed class DxRuntime(
    SqliteConnection conn,
    string           root,
    string           sessionId,
    IgnoreSet        ignoreSet,
    IDxLogger?       logger = null)
{
    private readonly IDxLogger _log = logger ?? NullDxLogger.Instance;

    // ── Factory ───────────────────────────────────────────────────────────

    public static DxRuntime Open(string root, string? sessionId = null, IDxLogger? logger = null)
    {
        var dxDir = Path.Combine(root, ".dx");
        if (!Directory.Exists(dxDir))
            throw new DxException(DxError.WorkspaceNotInitialized,
                $"No DX workspace found at {root}. Run 'dx init' first.");

        var conn = DxDatabase.Open(root);
        DxDatabase.Migrate(conn);

        // Resolve session
        sessionId ??= conn.ExecuteScalar<string>(
            "SELECT session_id FROM sessions WHERE closed_utc IS NULL ORDER BY created_utc DESC LIMIT 1")
            ?? throw new DxException(DxError.SessionNotFound,
                "No active session found. Run 'dx init' to start one.");

        var ignoreJson = conn.ExecuteScalar<string>(
            "SELECT ignore_set_json FROM sessions WHERE session_id = @sid",
            new { sid = sessionId })
            ?? throw new DxException(DxError.SessionNotFound,
                $"Session not found: {sessionId}");

        var ignoreSet = IgnoreSet.Deserialize(ignoreJson);

        return new DxRuntime(conn, root, sessionId, ignoreSet, logger);
    }

    // ── Init ──────────────────────────────────────────────────────────────

    public static string Init(
        string root,
        string sessionId,
        string? artifactsDir,
        IEnumerable<string>? exclude,
        bool includeBuildOutput,
        IDxLogger? logger = null)
    {
        var log = logger ?? NullDxLogger.Instance;

        if (!Directory.Exists(root))
            throw new DxException(DxError.InvalidArgument,
                $"Root directory does not exist: {root}");

        var dxDir = Path.Combine(root, ".dx");
        if (Directory.Exists(dxDir) && File.Exists(Path.Combine(dxDir, "dx.db")))
            throw new DxException(DxError.InvalidArgument,
                "Workspace already initialized. Use 'dx session new' for a new session.");

        Directory.CreateDirectory(dxDir);

        var ignoreSet = IgnoreSet.Build(root, artifactsDir, exclude, includeBuildOutput);

        using var conn = DxDatabase.Open(root);
        DxDatabase.Migrate(conn);

        conn.Execute(
            """
            INSERT INTO sessions (session_id, root, artifacts_dir, ignore_set_json, created_utc)
            VALUES (@sid, @root, @arts, @ign, @t)
            """,
            new
            {
                sid  = sessionId,
                root = Path.GetFullPath(root),
                arts = artifactsDir,
                ign  = ignoreSet.Serialize(),
                t    = DxDatabase.UtcNow()
            });

        // Genesis snap T0000
        var manifest = ManifestBuilder.Build(root, ignoreSet);
        var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

        using var tx = conn.BeginTransaction();

        conn.Execute("INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h, @t)",
            new { h = snapHash, t = DxDatabase.UtcNow() }, tx);

        foreach (var entry in manifest)
            BlobStore.InsertFile(conn, tx, entry.AbsolutePath);

        foreach (var entry in manifest)
            conn.Execute(
                """
                INSERT OR IGNORE INTO snap_files
                    (session_id, snap_hash, path, content_hash, size_bytes)
                VALUES (@sid, @sh, @p, @ch, @sz)
                """,
                new { sid = sessionId, sh = snapHash, p = entry.Path,
                      ch = entry.ContentHash, sz = entry.Size }, tx);

        HandleAssigner.AssignHandle(conn, tx, sessionId, snapHash, DxDatabase.UtcNow());

        conn.Execute(
            """
            INSERT INTO session_state (session_id, head_snap_hash, updated_utc)
            VALUES (@sid, @sh, @t)
            """,
            new { sid = sessionId, sh = snapHash, t = DxDatabase.UtcNow() }, tx);

        tx.Commit();

        log.Info($"Initialized DX workspace at {root}");
        log.Info($"Session:  {sessionId}");
        log.Info($"Genesis:  T0000 ({manifest.Count} files)");

        return "T0000";
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    public async Task<Protocol.DispatchResult> ApplyAsync(
        Protocol.DxDocument doc,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var dispatcher = new Protocol.DxDispatcher(
            conn, root, ignoreSet, sessionId, _log);

        return await dispatcher.DispatchAsync(doc, dryRun, progress, ct);
    }

    // ── Snap graph queries ────────────────────────────────────────────────

    public IReadOnlyList<SnapInfo> ListSnaps()
        => conn.Query<SnapInfo>(
            """
            SELECT h.handle AS Handle,
                   h.seq    AS Seq,
                   h.created_utc AS CreatedUtc,
                   CASE WHEN s.head_snap_hash = h.snap_hash THEN 1 ELSE 0 END AS IsHead
            FROM snap_handles h
            JOIN session_state s ON s.session_id = h.session_id
            WHERE h.session_id = @sid
            ORDER BY h.seq ASC
            """,
            new { sid = sessionId }).ToList();

    public string? GetHead()
        => conn.ExecuteScalar<string>(
            """
            SELECT h.handle
            FROM session_state ss
            JOIN snap_handles h ON h.snap_hash = ss.head_snap_hash
                               AND h.session_id = ss.session_id
            WHERE ss.session_id = @sid
            """,
            new { sid = sessionId });

    public IReadOnlyList<SnapFileInfo> GetSnapFiles(string handle)
    {
        var snapHash = ResolveHandle(handle);
        return conn.Query<SnapFileInfo>(
            """
            SELECT path AS Path, size_bytes AS SizeBytes
            FROM snap_files
            WHERE snap_hash = @sh AND session_id = @sid
            ORDER BY path ASC
            """,
            new { sh = snapHash, sid = sessionId }).ToList();
    }

    public IReadOnlyList<DiffEntry> Diff(string handleA, string handleB, string? filterPath = null)
    {
        var hashA = ResolveHandle(handleA);
        var hashB = ResolveHandle(handleB);

        var filesA = conn.Query<(string Path, byte[] ContentHash)>(
            "SELECT path, content_hash FROM snap_files WHERE snap_hash = @sh AND session_id = @sid",
            new { sh = hashA, sid = sessionId })
            .ToDictionary(r => r.Path, r => r.ContentHash, StringComparer.Ordinal);

        var filesB = conn.Query<(string Path, byte[] ContentHash)>(
            "SELECT path, content_hash FROM snap_files WHERE snap_hash = @sh AND session_id = @sid",
            new { sh = hashB, sid = sessionId })
            .ToDictionary(r => r.Path, r => r.ContentHash, StringComparer.Ordinal);

        var result = new List<DiffEntry>();
        var allPaths = filesA.Keys.Union(filesB.Keys).OrderBy(p => p).ToList();

        foreach (var path in allPaths)
        {
            if (filterPath is not null && !path.StartsWith(filterPath, StringComparison.Ordinal))
                continue;

            var inA = filesA.TryGetValue(path, out var hashA2);
            var inB = filesB.TryGetValue(path, out var hashB2);

            var status = (inA, inB) switch
            {
                (true,  false) => DiffStatus.Deleted,
                (false, true)  => DiffStatus.Added,
                (true,  true) when DxHash.Equal(hashA2!, hashB2!) => DiffStatus.Unchanged,
                _ => DiffStatus.Modified,
            };

            if (status == DiffStatus.Unchanged) continue;

            result.Add(new DiffEntry(path, status));
        }

        return result;
    }

    // ── Checkout ──────────────────────────────────────────────────────────

    public string Checkout(string targetHandle)
    {
        var targetHash = ResolveHandle(targetHandle);

        using var dxLock  = DxLock.Acquire(root);
        var currentHead   = GetCurrentHeadHash();
        var currentHandle = HandleAssigner.ReverseResolve(conn, sessionId, currentHead) ?? "?";

        var engine = new RollbackEngine(conn, root, ignoreSet);
        engine.RestoreTo(targetHash);

        // New snap (checkout-of)
        var manifest = ManifestBuilder.Build(root, ignoreSet);
        var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

        var writer    = new SnapshotWriter(conn);
        var newHandle = writer.Persist(sessionId, snapHash, manifest);

        // Mark checkout_of on the new snap handle row
        // (stored in snap_handles as metadata via a note — lightweight for v1)
        _log.Info($"Checked out {targetHandle} → {newHandle}");

        return newHandle;
    }

    // ── Session log ───────────────────────────────────────────────────────

    public IReadOnlyList<LogEntry> GetLog(int limit = 100)
        => conn.Query<LogEntry>(
            """
            SELECT id AS Id, direction AS Direction, snap_handle AS SnapHandle,
                   tx_success AS TxSuccess, created_at AS CreatedAt
            FROM session_log
            WHERE session_id = @sid
            ORDER BY id DESC
            LIMIT @limit
            """,
            new { sid = sessionId, limit }).ToList();

    // ── Helpers ───────────────────────────────────────────────────────────

    private byte[] ResolveHandle(string handle)
        => HandleAssigner.Resolve(conn, sessionId, handle)
           ?? throw new DxException(DxError.SnapNotFound,
               $"Snap not found: {handle}");

    private byte[] GetCurrentHeadHash()
        => conn.ExecuteScalar<byte[]>(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = sessionId })
           ?? throw new DxException(DxError.SessionNotFound,
               $"No HEAD for session: {sessionId}");
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record SnapInfo(string Handle, int Seq, string CreatedUtc, bool IsHead);
public sealed record SnapFileInfo(string Path, long SizeBytes);
public sealed record LogEntry(int Id, string Direction, string? SnapHandle, int TxSuccess, string CreatedAt);

public enum DiffStatus { Added, Modified, Deleted, Unchanged }
public sealed record DiffEntry(string Path, DiffStatus Status);
