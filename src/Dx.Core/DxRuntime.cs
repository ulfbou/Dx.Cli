using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// The primary runtime facade for DX workspace operations, encapsulating the database
/// connection, session context, and ignore-set configuration required to execute
/// transactions, query snapshots, and materialise snap states for isolated execution.
/// </summary>
/// <param name="conn">An open database connection to the workspace <c>snap.db</c>.</param>
/// <param name="root">The absolute workspace root path.</param>
/// <param name="sessionId">The identifier of the active session.</param>
/// <param name="ignoreSet">The file exclusion rules for the active session.</param>
/// <param name="logger">
/// An optional diagnostic logger. When <see langword="null"/>, <see cref="NullDxLogger"/>
/// is used and all output is suppressed.
/// </param>
public sealed class DxRuntime(
    SqliteConnection conn,
    string root,
    string sessionId,
    IgnoreSet ignoreSet,
    IDxLogger? logger = null)
{
    private readonly IDxLogger _log = logger ?? NullDxLogger.Instance;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a <see cref="DxRuntime"/> instance for an existing workspace, resolving
    /// the most recent active session when <paramref name="sessionId"/> is not specified.
    /// </summary>
    /// <param name="root">The workspace root directory.</param>
    /// <param name="sessionId">
    /// The session identifier to open. When <see langword="null"/>, the most recently
    /// created open session is used.
    /// </param>
    /// <param name="logger">An optional diagnostic logger.</param>
    /// <returns>A fully initialised <see cref="DxRuntime"/> bound to the resolved session.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.WorkspaceNotInitialized"/> when no <c>.dx/</c>
    /// directory is found, or with <see cref="DxError.SessionNotFound"/> when no active
    /// session exists or the specified session cannot be found.
    /// </exception>
    public static DxRuntime Open(string root, string? sessionId = null, IDxLogger? logger = null)
    {
        var dxDir = Path.Combine(root, ".dx");
        if (!Directory.Exists(dxDir))
            throw new DxException(DxError.WorkspaceNotInitialized,
                $"No DX workspace found at {root}. Run 'dxs init' first.");

        var conn = DxDatabase.Open(root);
        DxDatabase.Migrate(conn);

        // Resolve session
        sessionId ??= conn.ExecuteScalar<string>(
            "SELECT session_id FROM sessions WHERE closed_utc IS NULL ORDER BY created_utc DESC LIMIT 1")
            ?? throw new DxException(DxError.SessionNotFound,
                "No active session found. Run 'dxs init' to start one.");

        var ignoreJson = conn.ExecuteScalar<string>(
            "SELECT ignore_set_json FROM sessions WHERE session_id = @sid",
            new { sid = sessionId })
            ?? throw new DxException(DxError.SessionNotFound,
                $"Session not found: {sessionId}");

        var ignoreSet = IgnoreSet.Deserialize(ignoreJson);

        return new DxRuntime(conn, root, sessionId, ignoreSet, logger);
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new DX workspace at the given root path, creating the <c>.dx/</c>
    /// directory, registering the genesis session, and taking the initial snapshot
    /// <c>T0000</c> of the current working tree.
    /// </summary>
    /// <param name="root">The directory to initialise as a workspace.</param>
    /// <param name="sessionId">The identifier to assign to the genesis session.</param>
    /// <param name="artifactsDir">
    /// An optional directory to exclude from all snapshots (e.g. a CI artifact output dir).
    /// </param>
    /// <param name="exclude">
    /// An optional sequence of additional paths to exclude from snapshots.
    /// </param>
    /// <param name="includeBuildOutput">
    /// When <see langword="true"/>, <c>bin/</c> and <c>obj/</c> are included in snapshots.
    /// </param>
    /// <param name="logger">An optional diagnostic logger.</param>
    /// <returns>The handle assigned to the genesis snapshot (always <c>T0000</c>).</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.WorkspaceAlreadyInitialized"/> when a <c>.dx/</c>
    /// directory already exists at or above the target path.
    /// </exception>
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

        // 3. Begin transaction for genesis snap
        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute(
                """
                INSERT INTO sessions (session_id, root, artifacts_dir, ignore_set_json, created_utc)
                VALUES (@sid, @root, @arts, @ign, @t)
                """,
                new
                {
                    sid  = sessionId,
                    root = rootPath,
                    arts = artifactsDir,
                    ign  = ignoreSet.Serialize(),
                    t    = now
                }, tx);

            var manifest = ManifestBuilder.Build(rootPath, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            conn.Execute("INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h, @t)",
                new { h = snapHash, t = now }, tx);

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
                        sh  = snapHash,
                        p   = entry.Path,
                        ch  = entry.ContentHash,
                        sz  = entry.Size
                    }, tx);
            }

            var handle = HandleAssigner.AssignHandle(conn, tx, sessionId, snapHash, now);

            conn.Execute(
                """
                INSERT INTO session_log (session_id, direction, document, snap_handle, tx_success, created_at)
                VALUES (@sid, 'tool', 'dxs init', @handle, 1, @t)
                """,
                new { sid = sessionId, handle, t = now }, tx);

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

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a parsed <see cref="Protocol.DxDocument"/> as an atomic transaction,
    /// writing mutations to the working tree and creating a new snapshot on success.
    /// </summary>
    /// <param name="doc">The parsed DX document to apply.</param>
    /// <param name="dryRun">
    /// When <see langword="true"/>, validation is performed but no changes are written
    /// and no snapshot is created.
    /// </param>
    /// <param name="progress">
    /// An optional progress sink that receives human-readable status strings as each
    /// block is applied.
    /// </param>
    /// <param name="ct">A cancellation token that can interrupt the operation.</param>
    /// <returns>
    /// A <see cref="Protocol.DispatchResult"/> describing the outcome, including the
    /// new snapshot handle on success or an error message on failure.
    /// </returns>
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

    // ── Snap graph queries ────────────────────────────────────────────────────

    /// <summary>
    /// Returns an ordered list of all snapshot handles registered in the current session,
    /// from oldest (T0000) to newest, with the HEAD marker resolved.
    /// </summary>
    /// <returns>A read-only list of <see cref="SnapInfo"/> records.</returns>
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

    /// <summary>
    /// Returns the handle string of the current HEAD snapshot for the active session,
    /// or <see langword="null"/> when no snapshot has been committed yet.
    /// </summary>
    /// <returns>A handle string such as <c>T0003</c>, or <see langword="null"/>.</returns>
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

    /// <summary>
    /// Returns the file manifest for the snapshot identified by <paramref name="handle"/>.
    /// </summary>
    /// <param name="handle">The snapshot handle to inspect (e.g. <c>T0002</c>).</param>
    /// <returns>
    /// A read-only list of <see cref="SnapFileInfo"/> records, ordered by path.
    /// </returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.SnapNotFound"/> when the handle does not exist.
    /// </exception>
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

    /// <summary>
    /// Computes the file-level diff between two snapshots, returning only entries that
    /// differ (added, deleted, or modified). Unchanged files are omitted.
    /// </summary>
    /// <param name="handleA">The baseline snapshot handle.</param>
    /// <param name="handleB">The candidate snapshot handle.</param>
    /// <param name="filterPath">
    /// An optional path prefix to restrict the diff to a specific subdirectory or file.
    /// </param>
    /// <returns>A read-only list of <see cref="DiffEntry"/> records describing each change.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.SnapNotFound"/> when either handle does not exist.
    /// </exception>
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

    // ── Checkout ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the workspace working tree to the state recorded in the specified snapshot,
    /// then records the resulting tree as a new snapshot (or reuses an existing one if the
    /// content is identical).
    /// </summary>
    /// <param name="targetHandle">The handle of the snapshot to restore (e.g. <c>T0001</c>).</param>
    /// <returns>The handle of the snapshot that represents the post-checkout state.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.SnapNotFound"/> when the handle does not exist, or
    /// with <see cref="DxError.PendingTransactionOnOtherSession"/> when the workspace lock
    /// cannot be acquired.
    /// </exception>
    public string Checkout(string targetHandle)
    {
        var targetHash = ResolveHandle(targetHandle);

        using var dxLock = DxLock.Acquire(root);
        var currentHead = GetCurrentHeadHash();
        var currentHandle = HandleAssigner.ReverseResolve(conn, sessionId, currentHead) ?? "?";

        var engine = new RollbackEngine(conn, root, ignoreSet);
        engine.RestoreTo(targetHash);

        var manifest = ManifestBuilder.Build(root, ignoreSet);
        var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

        var writer = new SnapshotWriter(conn);
        var newHandle = writer.Persist(sessionId, snapHash, manifest);

        _log.Info($"Checked out {targetHandle} → {newHandle}");

        return newHandle;
    }

    // ── Session log ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the most recent transaction log entries for the active session, ordered
    /// from newest to oldest.
    /// </summary>
    /// <param name="limit">The maximum number of entries to return. Defaults to <c>100</c>.</param>
    /// <returns>A read-only list of <see cref="LogEntry"/> records.</returns>
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

    // ── Snap materialisation for isolated execution ───────────────────────────

    /// <summary>
    /// Materialises the specified snapshot into a temporary directory on disk, suitable
    /// for use by <c>dxs run --snap</c> and <c>dxs eval</c> as an isolated execution context.
    /// </summary>
    /// <param name="handle">The snapshot handle to materialise (e.g. <c>T0002</c>).</param>
    /// <returns>
    /// A task that resolves to the absolute path of the temporary directory. The caller is
    /// responsible for deleting the directory when it is no longer needed.
    /// </returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.SnapNotFound"/> when the handle does not exist.
    /// </exception>
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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a snapshot handle to its raw SHA-256 hash, throwing
    /// <see cref="DxError.SnapNotFound"/> if the handle is unknown.
    /// </summary>
    private byte[] ResolveHandle(string handle)
        => HandleAssigner.Resolve(conn, sessionId, handle)
           ?? throw new DxException(DxError.SnapNotFound,
               $"Snap not found: {handle}");

    /// <summary>
    /// Returns the raw SHA-256 hash of the current HEAD snapshot, throwing
    /// <see cref="DxError.SessionNotFound"/> when the session has no HEAD record.
    /// </summary>
    private byte[] GetCurrentHeadHash()
        => conn.ExecuteScalar<byte[]>(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = sessionId })
           ?? throw new DxException(DxError.SessionNotFound,
               $"No HEAD for session: {sessionId}");
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents the metadata for a single snapshot within a session, as returned by
/// <see cref="DxRuntime.ListSnaps"/>.
/// </summary>
public class SnapInfo
{
    /// <summary>Parameterless constructor required by Dapper for materialisation.</summary>
    public SnapInfo() { }

    /// <summary>Gets or sets the human-readable snapshot handle (e.g. <c>T0003</c>).</summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based sequence number of this snapshot within the session.</summary>
    public long Seq { get; set; }

    /// <summary>Gets or sets the ISO 8601 UTC timestamp at which the snapshot was created.</summary>
    public string CreatedUtc { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this snapshot is the current HEAD of its session.
    /// </summary>
    public bool IsHead { get; set; }

    /// <summary>Initialises a fully populated <see cref="SnapInfo"/> instance.</summary>
    /// <param name="handle">The human-readable snapshot handle (e.g. <c>T0003</c>).</param>
    /// <param name="seq">The zero-based sequence number within the session.</param>
    /// <param name="createdUtc">The ISO 8601 UTC timestamp at which the snapshot was created.</param>
    /// <param name="isHead">
    /// <see langword="true"/> when this snapshot is the current HEAD of its session.
    /// </param>
    public SnapInfo(string handle, long seq, string createdUtc, bool isHead)
    {
        Handle = handle;
        Seq = seq;
        CreatedUtc = createdUtc;
        IsHead = isHead;
    }
}

/// <summary>
/// Represents a single file entry in a snapshot manifest, as returned by
/// <see cref="DxRuntime.GetSnapFiles"/>.
/// </summary>
/// <param name="Path">The normalised, forward-slash-separated relative path of the file.</param>
/// <param name="SizeBytes">The raw byte size of the file at the time the snapshot was taken.</param>
public sealed record SnapFileInfo(string Path, long SizeBytes);

/// <summary>
/// Represents a single entry in the session transaction log, as returned by
/// <see cref="DxRuntime.GetLog"/>.
/// </summary>
public sealed class LogEntry
{
    /// <summary>Gets or sets the auto-incremented log entry identifier.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the direction of the transaction: <c>llm</c> for documents originating
    /// from a language model, or <c>tool</c> for documents generated by CLI tooling.
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the snapshot handle produced by the transaction, or
    /// <see langword="null"/> when the transaction failed or produced no mutation.
    /// </summary>
    public string? SnapHandle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the transaction succeeded (<c>1</c>) or
    /// failed (<c>0</c>).
    /// </summary>
    public int TxSuccess { get; set; }

    /// <summary>Gets or sets the ISO 8601 UTC timestamp at which the transaction was recorded.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Parameterless constructor required by Dapper for materialisation.</summary>
    public LogEntry() { }

    /// <summary>Initialises a fully validated <see cref="LogEntry"/> instance.</summary>
    /// <param name="id">The auto-incremented log entry identifier.</param>
    /// <param name="direction">
    /// The transaction direction: <c>llm</c> for language-model documents or
    /// <c>tool</c> for CLI-generated documents.
    /// </param>
    /// <param name="snapHandle">
    /// The snapshot handle produced by the transaction, or <see langword="null"/>
    /// when the transaction failed or produced no mutation.
    /// </param>
    /// <param name="txSuccess">
    /// <c>1</c> when the transaction committed successfully; <c>0</c> when it failed.
    /// </param>
    /// <param name="createdAt">The ISO 8601 UTC timestamp at which the transaction was recorded.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> or <paramref name="createdAt"/> is null or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="txSuccess"/> is not <c>0</c> or <c>1</c>.
    /// </exception>
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

/// <summary>Classifies the change status of a file between two snapshots.</summary>
public enum DiffStatus
{
    /// <summary>The file exists in the candidate snapshot but not in the baseline.</summary>
    Added,

    /// <summary>The file exists in both snapshots but its content differs.</summary>
    Modified,

    /// <summary>The file exists in the baseline snapshot but not in the candidate.</summary>
    Deleted,

    /// <summary>The file exists in both snapshots and its content is identical.</summary>
    Unchanged,
}

/// <summary>
/// Represents a single changed file entry in a snapshot diff, as returned by
/// <see cref="DxRuntime.Diff"/>.
/// </summary>
/// <param name="Path">The normalised, forward-slash-separated relative path of the changed file.</param>
/// <param name="Status">The nature of the change.</param>
public sealed record DiffEntry(string Path, DiffStatus Status);

/// <summary>
/// Internal Dapper mapping record used when materialising snapshot file entries
/// during <see cref="DxRuntime.MaterializeSnapAsync"/>.
/// </summary>
file sealed class SnapMaterializeRow
{
    /// <summary>Gets or sets the normalised relative path of the file.</summary>
    public string Path { get; set; } = "";

    /// <summary>Gets or sets the SHA-256 content hash used to retrieve the blob from storage.</summary>
    public byte[] ContentHash { get; set; } = [];
}
