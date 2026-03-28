using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// Persists a new snapshot to the workspace database, including the snap row, all file
/// content blobs, the snap-file manifest, a session-scoped handle, and an updated HEAD
/// pointer.
/// </summary>
/// <remarks>
/// <para>
/// All writes are performed inside a single database transaction. When an
/// <paramref name="outerTx"/> is provided the writer participates in it; otherwise it
/// creates and manages its own transaction.
/// </para>
/// <para>
/// Every operation is idempotent at the row level: <c>INSERT OR IGNORE</c> is used
/// throughout so that re-persisting an identical snapshot (e.g. after a no-op apply)
/// is safe and produces no duplicate data.
/// </para>
/// </remarks>
/// <param name="conn">An open database connection to the workspace <c>snap.db</c>.</param>
public sealed class SnapshotWriter
{
    private readonly SqliteConnection _conn;

    public SnapshotWriter(SqliteConnection conn) => _conn = conn;

    /// <summary>
    /// Writes the snapshot described by <paramref name="manifest"/> to the database and
    /// advances the session HEAD to the new snapshot.
    /// </summary>
    /// <param name="sessionId">The identifier of the session that owns this snapshot.</param>
    /// <param name="snapHash">
    /// The 32-byte SHA-256 hash of the snapshot, computed by
    /// <see cref="ManifestBuilder.ComputeSnapHash"/>.
    /// </param>
    /// <param name="manifest">
    /// The ordered list of file entries that make up the snapshot, as produced by
    /// <see cref="ManifestBuilder.Build"/>.
    /// </param>
    /// <param name="outerTx">
    /// An optional enclosing transaction. When <see langword="null"/> the writer opens
    /// and commits its own transaction.
    /// </param>
    /// <returns>
    /// The human-readable handle assigned to the new snapshot (e.g. <c>T0005</c>),
    /// or the pre-existing handle when the snapshot hash is already registered.
    /// </returns>
    public async Task<string> PersistAsync(
        string sessionId, 
        byte[] snapHash, 
        IReadOnlyList<ManifestEntry> manifest, 
        CancellationToken ct = default, 
        SqliteTransaction? outerTx = null)
    {
        var ownTx = outerTx is null;
        var tx = outerTx ?? _conn.BeginTransaction();

        try
        {
            // 1. Snap row (idempotent)
            _conn.Execute(
                "INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h, @t)",
                new { h = snapHash, t = DxDatabase.UtcNow() }, tx);

            // 2. File content (streaming, deduplicated)
            var store = new Storage.SqliteContentStore(_conn);
            foreach (var e in manifest)
            {
                using var fs = File.OpenRead(e.AbsolutePath);
                await store.StoreAsync(e.ContentHash, fs, e.Size, ct);
            }

            // 3. snap_files manifest
            foreach (var entry in manifest)
                _conn.Execute(
                    """
                    INSERT OR IGNORE INTO snap_files
                        (session_id, snap_hash, path, content_hash, size_bytes)
                    VALUES (@sid, @sh, @path, @ch, @sz)
                    """,
                    new
                    {
                        sid  = sessionId,
                        sh   = snapHash,
                        path = entry.Path,
                        ch   = entry.ContentHash,
                        sz   = entry.Size
                    }, tx);

            // 4. T-handle assignment (optimistic retry inside HandleAssigner)
            var handle = HandleAssigner.AssignHandle(
                _conn, tx, sessionId, snapHash, DxDatabase.UtcNow());

            // 5. HEAD upsert
            _conn.Execute(
                """
                INSERT INTO session_state (session_id, head_snap_hash, updated_utc)
                VALUES (@sid, @sh, @t)
                ON CONFLICT(session_id) DO UPDATE
                    SET head_snap_hash = excluded.head_snap_hash,
                        updated_utc    = excluded.updated_utc
                """,
                new { sid = sessionId, sh = snapHash, t = DxDatabase.UtcNow() }, tx);

            if (ownTx) tx.Commit();

            return handle;
        }
        catch
        {
            if (ownTx) tx.Rollback();
            throw;
        }
        finally
        {
            if (ownTx) tx.Dispose();
        }
    }
}
