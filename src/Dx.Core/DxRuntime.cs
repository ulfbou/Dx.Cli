using Dapper;

using Dx.Core.Protocol;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// The primary runtime facade for DX workspace operations, encapsulating the database
/// connection, session context, and ignore-set configuration required to execute
/// transactions, query snapshots, and materialise snap states for isolated execution.
/// </summary>
/// <param name="_conn">An open database connection to the workspace <c>snap.db</c>.</param>
/// <param name="_root">The absolute workspace root path.</param>
/// <param name="_sessionId">The identifier of the active session.</param>
/// <param name="_ignoreSet">The file exclusion rules for the active session.</param>
/// <param name="_logger">
/// An optional diagnostic logger. When <see langword="null"/>, <see cref="NullDxLogger"/>
/// is used and all output is suppressed.
/// </param>
public sealed class DxRuntime
{
    private readonly IDxLogger _logger;
    private readonly SqliteConnection _conn;
    private readonly string _root;
    private readonly string _sessionId;
    public IgnoreSet IgnoreSet { get; init; }

    private DxRuntime(
        SqliteConnection conn,
        string root,
        string sessionId,
        IgnoreSet ignoreSet,
        IDxLogger? logger = null)
    {
        _conn = conn;
        _root = root;
        _sessionId = sessionId;
        IgnoreSet = ignoreSet;
        _logger = logger ?? new NullDxLogger();
    }

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
        if (!IsWorkspace(root))
        {
            throw new DxException(DxError.WorkspaceNotInitialized,
                $"No DX workspace found at {root}. Run 'dxs init' first.");
        }

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

    /// <summary>
    /// Determines whether the specified directory is the root of a DX workspace by checking
    /// for the presence of the <c>.dx/</c> directory and the workspace database file
    /// <c>snap.db</c>.
    /// </summary>
    /// <param name="root">The directory to check.</param>
    /// <returns><see langword="true"/> if the directory is a DX workspace root; otherwise, 
    /// <see langword="false"/>.</returns>
    public static bool IsWorkspace(string root)
    {
        var dxDir = Path.Combine(root, ".dx");
        var dbPath = Path.Combine(dxDir, "snap.db");

        return Directory.Exists(dxDir) && File.Exists(dbPath);
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
    /// <exception cref="NotSupportedException">
    /// Thrown unconditionally to prevent accidental misuse. This method is deprecated in favour of
    /// the more robust and flexible combination of <see cref="WorkspaceInitializer"/> and
    /// <see cref="SessionGenesisCreator"/>, which together provide a unified, deterministic source
    /// of truth for workspace initialisation logic and ensure that all genesis creation flows
    /// (e.g. <c>dxs init</c>, <c>dx session new</c>, and programmatic API usage) share the same 
    /// underlying implementation.
    /// </exception>
    [Obsolete("DxRuntime.Init is deprecated. Use WorkspaceInitializer + SessionGenesisCreator.", true)]
    public static void Init_Obsolete()
    {
        throw new NotSupportedException(
            "DxRuntime.Init is removed. Use WorkspaceInitializer + SessionGenesisCreator.");
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
    /// <param name="options">
    /// Optional per-invocation overrides for base-mismatch behaviour and run timeout.
    /// When <see langword="null"/>, workspace configuration defaults apply.
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
        Protocol.ApplyOptions? options = null,
        CancellationToken ct = default)
    {

        // Always ensure pending_transaction is clean before any new dispatch
        _conn.Execute("DELETE FROM pending_transaction WHERE id = 1");

        var dispatcher = new Protocol.DxDispatcher(
            _conn,
            _root,
            IgnoreSet,
            _sessionId,
            _logger);

        return await dispatcher.DispatchAsync(
            doc,
            dryRun,
            progress,
            options,
            ct);
    }

    // ── Snap graph queries ────────────────────────────────────────────────────

    /// <summary>
    /// Returns an ordered list of all snapshot handles registered in the current session,
    /// from oldest (T0000) to newest, with the HEAD marker resolved.
    /// </summary>
    /// <returns>A read-only list of <see cref="SnapInfo"/> records.</returns>
    public IReadOnlyList<SnapInfo> ListSnaps()
        => _conn.Query<SnapInfo>(
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
            new { sid = _sessionId }).ToList();

    /// <summary>
    /// Returns the handle string of the current HEAD snapshot for the active session,
    /// or <see langword="null"/> when no snapshot has been committed yet.
    /// </summary>
    /// <returns>A handle string such as <c>T0003</c>, or <see langword="null"/>.</returns>
    public string? GetHead()
        => _conn.ExecuteScalar<string>(
            """
            SELECT h.handle
            FROM session_state ss
            JOIN snap_handles h ON h.snap_hash = ss.head_snap_hash
                               AND h.session_id = ss.session_id
            WHERE ss.session_id = @sid
            """,
            new { sid = _sessionId });

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
        return _conn.Query<SnapFileInfo>(
            """
            SELECT path AS Path, size_bytes AS SizeBytes
            FROM snap_files
            WHERE snap_hash = @sh AND session_id = @sid
            ORDER BY path ASC
            """,
            new { sh = snapHash, sid = _sessionId }).ToList();
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

        var filesA = _conn.Query<(string Path, byte[] ContentHash)>(
            "SELECT path, content_hash FROM snap_files WHERE snap_hash = @sh AND session_id = @sid",
            new { sh = hashA, sid = _sessionId })
            .ToDictionary(r => r.Path, r => r.ContentHash, StringComparer.Ordinal);

        var filesB = _conn.Query<(string Path, byte[] ContentHash)>(
            "SELECT path, content_hash FROM snap_files WHERE snap_hash = @sh AND session_id = @sid",
            new { sh = hashB, sid = _sessionId })
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
    /// content is identical) and writes a session_log entry.
    /// </summary>
    /// <param name="targetHandle">The handle of the snapshot to restore (e.g. <c>T0001</c>).</param>
    /// <returns>A task that represents the handle of the snapshot that represents the post-checkout 
    /// state.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.SnapNotFound"/> when the handle does not exist, or
    /// with <see cref="DxError.PendingTransactionOnOtherSession"/> when the workspace lock
    /// cannot be acquired.
    /// </exception>
    public async Task<string> CheckoutAsync(string targetHandle, CancellationToken ct = default)
    {
        var targetHash = ResolveHandle(targetHandle);

        var lockFile = Path.Combine(_root, ".dx", "snaps.lock");
        await using var dxLock = await DxLock.AcquireAsync(lockFile, TimeSpan.FromSeconds(5), ct);
        var currentHead = GetCurrentHeadHash();

        var engine = new RollbackEngine(_conn, _root, IgnoreSet);
        engine.RestoreTo(targetHash);

        var manifest = ManifestBuilder.Build(_root, IgnoreSet);
        var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

        var writer = new SnapshotWriter(_conn);
        var newHandle = await writer.PersistAsync(_sessionId, snapHash, manifest, ct:ct);

        // Invariant: checkout must be logged (tool direction)
        _conn.Execute(
            """
            INSERT INTO session_log
            (session_id, direction, document, snap_handle, tx_success, created_at)
            VALUES (@sid, 'tool', @doc, @handle, 1, @t)
            """,
            new
            {
                sid = _sessionId,
                doc = $"dxs snap checkout {targetHandle}",
                handle = newHandle,
                t = DxDatabase.UtcNow()
            });

        // Invariant: ALL state mutations must be reflected in session_log.
        // Checkout is a state mutation — it advances HEAD — and must be logged.
        _conn.Execute(
            """
        INSERT INTO session_log
            (session_id, direction, document, snap_handle, tx_success, created_at)
        VALUES (@sid, 'tool', @doc, @handle, 1, @t)
        """,
            new
            {
                sid = _sessionId,
                doc = $"dxs snap checkout {targetHandle}",
                handle = newHandle,
                t = DxDatabase.UtcNow()
            });

        _logger.Info($"Checked out {targetHandle} → {newHandle}");

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
        => _conn.Query<LogEntry>(
            """
            SELECT id AS Id, direction AS Direction, snap_handle AS SnapHandle,
                   tx_success AS TxSuccess, created_at AS CreatedAt
            FROM session_log
            WHERE session_id = @sid
            ORDER BY id DESC
            LIMIT @limit
            """,
            new { sid = _sessionId, limit }).ToList();

    // ── Snap materialisation for isolated execution ───────────────────────────

    /// <summary>
    /// Materialises the specified snapshot into a temporary directory on disk, suitable
    /// for use by <c>dxs run --snap</c> and <c>dxs eval</c> as an isolated execution
    /// context.
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

        var files = _conn.Query<Core.SnapMaterializeRow>(
            """
            SELECT sf.path         AS Path,
                   sf.content_hash AS ContentHash
            FROM snap_files sf
            WHERE sf.snap_hash   = @sh
              AND sf.session_id  = @sid
            ORDER BY sf.path ASC
            """,
            new { sh = snapHash, sid = _sessionId });

        var store = new Storage.SqliteContentStore(_conn);
        foreach (var file in files)
        {
            var absPath = DxPath.ToAbsolute(tempDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

            using var src = store.OpenRead(file.ContentHash);
            using var dst = File.OpenWrite(absPath);
            src.CopyTo(dst, bufferSize: 81_920);
        }

        _logger.Debug($"Materialized {handle} → {tempDir}");

        return Task.FromResult(tempDir);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a snapshot handle to its raw SHA-256 hash, throwing
    /// <see cref="DxError.SnapNotFound"/> if the handle is unknown.
    /// </summary>
    private byte[] ResolveHandle(string handle)
        => HandleAssigner.Resolve(_conn, _sessionId, handle)
           ?? throw new DxException(DxError.SnapNotFound,
               $"Snap not found: {handle}");

    /// <summary>
    /// Returns the raw SHA-256 hash of the current HEAD snapshot, throwing
    /// <see cref="DxError.SessionNotFound"/> when the session has no HEAD record.
    /// </summary>
    private byte[] GetCurrentHeadHash()
        => _conn.ExecuteScalar<byte[]>(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = _sessionId })
           ?? throw new DxException(DxError.SessionNotFound,
               $"No HEAD for session: {_sessionId}");
}

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
