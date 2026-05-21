using Dapper;

using Dx.Core.Execution;
using Dx.Core.Execution.Adapters;
using Dx.Core.Execution.Results;
using Dx.Core.Protocol;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

using System.IO;
using System.Reflection.Metadata;

namespace Dx.Core;

/// <summary>
/// The primary runtime facade for DX workspace operations, encapsulating the database
/// connection, session context, and ignore-set configuration required to execute
/// transactions, query snapshots, and materialise snap states for isolated execution.
/// </summary>
public sealed class DxRuntime
{
    private readonly IDxLogger _logger;
    private readonly IDxStore _store;
    private readonly SqliteConnection _connection;
    private readonly string _workspaceRoot;
    private readonly string _sessionId;
    private readonly DxDispatcher _dispatcher;

    /// <summary>Gets the file exclusion rules for the active session, used to determine which files
    /// are included in snapshots and visible to DX operations.</summary>
    public IgnoreSet IgnoreSet { get; init; }

    /// <param name="connection">An open database connection to the workspace <c>snap.db</c>.</param>
    /// <param name="workspaceRoot">The absolute workspace root path.</param>
    /// <param name="sessionId">The identifier of the active session.</param>
    /// <param name="ignoreSet">The file exclusion rules for the active session.</param>
    /// <param name="logger">
    /// An optional diagnostic logger. When <see langword="null"/>, <see cref="NullDxLogger"/>
    /// is used and all output is suppressed.
    /// </param>
    internal DxRuntime(
        SqliteConnection connection,
        string workspaceRoot,
        string sessionId,
        IgnoreSet ignoreSet,
        DxDispatcher dispatcher,
        IDxStore store,
        IDxLogger? logger = null)
    {
        _connection = connection;
        _workspaceRoot = workspaceRoot;
        _sessionId = sessionId;
        IgnoreSet = ignoreSet;
        _dispatcher = dispatcher;
        _store = store;
        _logger = logger ?? new NullDxLogger();
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a <see cref="DxRuntime"/> instance for an existing workspace, resolving
    /// the most recent active session when <paramref name="sessionId"/> is not specified.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root directory.</param>
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

    public static async Task<DxRuntime> OpenAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
        => await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);

    public static DxRuntime Create(SqliteConnection connection, string workspaceRoot, string sessionId, IgnoreSet ignoreSet, DxDispatcher dispatcher, IDxStore store, IDxLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection, nameof(connection));
        ArgumentNullException.ThrowIfNull(ignoreSet, nameof(ignoreSet));

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root must be provided.", nameof(workspaceRoot));

        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID must be provided.", nameof(sessionId));

        return new DxRuntime(connection, workspaceRoot, sessionId, ignoreSet, dispatcher, store, logger);
    }

    /// <summary>
    /// Determines whether the specified directory is the root of a DX workspace by checking
    /// for the presence of the <c>.dx/</c> directory and the workspace database file
    /// <c>snap.db</c>.
    /// </summary>
    /// <param name="workspaceRoot">The directory to check.</param>
    /// <returns><see langword="true"/> if the directory is a DX workspace root; otherwise, 
    /// <see langword="false"/>.</returns>
    public static bool IsWorkspace(string workspaceRoot)
    {
        var dxDir = Path.Combine(workspaceRoot, ".dx");
        var dbPath = Path.Combine(dxDir, "snap.db");

        return Directory.Exists(dxDir) && File.Exists(dbPath);
    }

    // Apply/Tool/Checkout/Materialize moved to DxDispatcher in T1.A4

    // ── Snap graph queries ────────────────────────────────────────────────────

    /// <summary>
    /// Returns an ordered list of all snapshot handles registered in the current session,
    /// from oldest (T0000) to newest, with the HEAD marker resolved.
    /// </summary>
    /// <returns>A read-only list of <see cref="SnapInfo"/> records.</returns>
    public IReadOnlyList<SnapInfo> ListSnaps()
        => _connection.Query<SnapInfo>(
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
            => _connection.ExecuteScalar<string>(
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
        return _connection.Query<SnapFileInfo>(
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

        var filesA = _connection.Query<(string Path, byte[] ContentHash)>(
            "SELECT path, content_hash FROM snap_files WHERE snap_hash = @sh AND session_id = @sid",
            new { sh = hashA, sid = _sessionId }
        ).ToDictionary(r => r.Path, r => r.ContentHash, StringComparer.Ordinal);

        var filesB = _connection.Query<(string Path, byte[] ContentHash)>(
            "SELECT path, content_hash FROM snap_files WHERE snap_hash = @sh AND session_id = @sid",
            new { sh = hashB, sid = _sessionId }
        ).ToDictionary(r => r.Path, r => r.ContentHash, StringComparer.Ordinal);

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


    // ── Session log ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the most recent transaction log entries for the active session, ordered
    /// from newest to oldest.
    /// </summary>
    /// <param name="limit">The maximum number of entries to return. Defaults to <c>100</c>.</param>
    /// <returns>A read-only list of <see cref="LogEntry"/> records.</returns>
    public IReadOnlyList<LogEntry> GetLog(int limit = 100)
        => _connection.Query<LogEntry>(
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

    // Tool and materialize helpers now live on DxDispatcher

    [Obsolete("Moved to DxDispatcher; use DxDispatcher.DispatchAsync or request APIs. Will be removed in a future PR.")]
    public async Task<DxResult> ApplyAsync(
        string rawText,
        string direction,
        bool isDryRun = false,
        IProgress<string>? progress = null,
        string? onBaseMismatch = null,
        int? runTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        return await _dispatcher.ApplyAsync(rawText, direction, isDryRun, progress, onBaseMismatch, runTimeoutSeconds, ct);
    }

    [Obsolete("Moved to DxDispatcher; use DxDispatcher.DispatchAsync or request APIs. Will be removed in a future PR.")]
    public async Task<string> CheckoutAsync(string targetHandle, CancellationToken ct = default)
    {
        return await _dispatcher.CheckoutAsync(targetHandle, ct);
    }

    [Obsolete("Moved to DxDispatcher; use DxDispatcher.DispatchAsync or request APIs. Will be removed in a future PR.")]
    public async Task<string> MaterializeSnapAsync(string handle, CancellationToken ct = default)
    {
        return await _dispatcher.MaterializeSnapAsync(handle, ct);
    }

    /// <summary>
    /// Resolves a snapshot handle to its raw SHA-256 hash, throwing
    /// <see cref="DxError.SnapNotFound"/> if the handle is unknown.
    /// </summary>
    private byte[] ResolveHandle(string handle)
        => HandleAssigner.Resolve(_connection, _sessionId, handle)
           ?? throw new DxException(DxError.SnapNotFound,
               $"Snap not found: {handle}");
}

/// <summary>
/// Internal Dapper mapping record used when materialising snapshot file entries
/// during <see cref="DxRuntime.MaterializeSnapAsync"/>.
/// </summary>
file sealed class SnapMaterializeRow
{
    /// <summary>Gets or sets the normalised relative path of the file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 content hash used to retrieve the blob from storage.</summary>
    public byte[] ContentHash { get; set; } = [];
}
