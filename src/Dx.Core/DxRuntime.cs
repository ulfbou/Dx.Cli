using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// High-level facade. CLI commands call this; they never touch DB or dispatcher directly.
/// </summary>
public sealed class DxRuntime(
    SqliteConnection conn,
    string root,
    string sessionId,
    IgnoreSet ignoreSet,
    IDxLogger? logger = null)
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
        var rootPath = Path.GetFullPath(root ?? ".");

        // 1. Guard against nested/existing workspaces
        // We check the target and parents to prevent accidental nested .dx folders
        var current = new DirectoryInfo(rootPath);

        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".dx")))
            {
                throw new DxException(DxError.WorkspaceAlreadyInitialized,
                    $"Workspace already initialized at or above: {current.FullName}");
            }
            current = current.Parent;
        }

        if (!Directory.Exists(rootPath))
            Directory.CreateDirectory(rootPath);

        var dxDir = Path.Combine(rootPath, ".dx");
        Directory.CreateDirectory(dxDir);

        // 2. Prepare workspace state
        var ignoreSet = IgnoreSet.Build(rootPath, artifactsDir, exclude, includeBuildOutput);
        var now = DxDatabase.UtcNow();

        using var conn = DxDatabase.Open(rootPath);
        DxDatabase.Migrate(conn);

        // 3. Begin Transaction for Genesis Snap
        using var tx = conn.BeginTransaction();
        try
        {
            // Register the session
            conn.Execute(
                """
            INSERT INTO sessions (session_id, root, artifacts_dir, ignore_set_json, created_utc)
            VALUES (@sid, @root, @arts, @ign, @t)
            """,
                new
                {
                    sid = sessionId,
                    root = rootPath,
                    arts = artifactsDir,
                    ign = ignoreSet.Serialize(),
                    t = now
                }, tx);

            // Build Genesis Manifest
            var manifest = ManifestBuilder.Build(rootPath, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            // Store Snapshot
            conn.Execute("INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h, @t)",
                new { h = snapHash, t = now }, tx);

            // Store File Contents and Links
            foreach (var entry in manifest)
            {
                BlobStore.InsertFile(conn, tx, entry.AbsolutePath);
                conn.Execute(
                    """
                INSERT OR IGNORE INTO snap_files 
                    (session_id, snap_hash, path, content_hash, size_bytes)
                VALUES (@sid, @sh, @p, @ch, @sz)
                """,
                    new
                    {
                        sid = sessionId,
                        sh = snapHash,
                        p = entry.Path,
                        ch = entry.ContentHash,
                        sz = entry.Size
                    }, tx);
            }

            // Assign T0000 handle
            var handle = HandleAssigner.AssignHandle(conn, tx, sessionId, snapHash, now);

            // UPDATE: Fix the "Ghost Log" bug - Record the 'init' event in the session log
            conn.Execute(
                """
            INSERT INTO session_log (session_id, direction, snap_handle, tx_success, created_at)
            VALUES (@sid, 'init', @handle, 1, @t)
            """,
                new { sid = sessionId, handle, t = now }, tx);

            // Set initial HEAD
            conn.Execute(
                """
            INSERT INTO session_state (session_id, head_snap_hash, updated_utc)
            VALUES (@sid, @sh, @t)
            """,
                new { sid = sessionId, sh = snapHash, t = now }, tx);

            tx.Commit();

            log.Info($"Initialized DX workspace at {rootPath}");
            log.Info($"Session:  {sessionId}");
            log.Info($"Genesis:  {handle} ({manifest.Count} files)");

            return handle;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
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
                (true, false) => DiffStatus.Deleted,
                (false, true) => DiffStatus.Added,
                (true, true) when DxHash.Equal(hashA2!, hashB2!) => DiffStatus.Unchanged,
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

        using var dxLock = DxLock.Acquire(root);
        var currentHead = GetCurrentHeadHash();
        var currentHandle = HandleAssigner.ReverseResolve(conn, sessionId, currentHead) ?? "?";

        var engine = new RollbackEngine(conn, root, ignoreSet);
        engine.RestoreTo(targetHash);

        // New snap (checkout-of)
        var manifest = ManifestBuilder.Build(root, ignoreSet);
        var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

        var writer = new SnapshotWriter(conn);
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

    // ── Snap materialization for isolated execution ───────────────────────

    /// <summary>
    /// Materializes a snap into a temporary directory for isolated execution
    /// (used by dx run --snap and dx eval). The caller is responsible for
    /// deleting the directory after use.
    /// Returns the absolute path to the materialized directory.
    /// </summary>
    public Task<string> MaterializeSnapAsync(string handle)
    {
        var snapHash = ResolveHandle(handle);

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"dx-snap-{handle}-{Path.GetRandomFileName()}");

        Directory.CreateDirectory(tempDir);

        var files = conn.Query<SnapMaterializeRow>(
            """
            SELECT sf.path         AS Path,
                   sf.content_hash AS ContentHash
            FROM snap_files sf
            WHERE sf.snap_hash   = @sh
              AND sf.session_id  = @sid
            ORDER BY sf.path ASC
            """,
            new { sh = snapHash, sid = sessionId });

        foreach (var file in files)
        {
            var absPath = DxPath.ToAbsolute(tempDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

            using var src = BlobStore.OpenRead(conn, file.ContentHash);
            using var dst = File.OpenWrite(absPath);
            src.CopyTo(dst, bufferSize: 81_920);
        }

        _log.Debug($"Materialized {handle} → {tempDir}");

        return Task.FromResult(tempDir);
    }

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

public class SnapInfo
{
    // Dapper uses this to populate the object
    public SnapInfo() { }

    public string Handle { get; set; } = string.Empty;
    public long Seq { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
    public bool IsHead { get; set; }

    // Optional: Keep your existing constructor if you had one
    public SnapInfo(string handle, long seq, string createdUtc, bool isHead)
    {
        Handle = handle;
        Seq = seq;
        CreatedUtc = createdUtc;
        IsHead = isHead;
    }
}
public sealed record SnapFileInfo(string Path, long SizeBytes);
public sealed class LogEntry
{
    public int Id { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string? SnapHandle { get; set; }
    public int TxSuccess { get; set; }
    public string CreatedAt { get; set; } = string.Empty;

    public LogEntry() { }

    public LogEntry(int id, string direction, string? snapHandle, int txSuccess, string createdAt)
    {
        if (string.IsNullOrWhiteSpace(direction))
            throw new ArgumentException("Direction cannot be null or empty.", nameof(direction));

        if (txSuccess != 0 && txSuccess != 1)
            throw new ArgumentOutOfRangeException(nameof(txSuccess), "TxSuccess must be either 0 or 1.");

        if (string.IsNullOrWhiteSpace(createdAt))
            throw new ArgumentException("CreatedAt cannot be null or empty.", nameof(createdAt));

        Id = id;
        Direction = direction;
        SnapHandle = snapHandle;
        TxSuccess = txSuccess;
        CreatedAt = createdAt;
    }
}

public enum DiffStatus { Added, Modified, Deleted, Unchanged }
public sealed record DiffEntry(string Path, DiffStatus Status);

file sealed class SnapMaterializeRow
{
    public string Path { get; set; } = "";
    public byte[] ContentHash { get; set; } = [];
}
