using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

public sealed class SnapshotWriter(SqliteConnection conn)
{
    /// <summary>
    /// Atomically persists snap, file_content blobs, snap_files manifest,
    /// snap handle, and updated HEAD. Returns the assigned T-handle.
    /// </summary>
    public string Persist(
        string sessionId,
        byte[] snapHash,
        IReadOnlyList<ManifestEntry> manifest,
        SqliteTransaction? outerTx = null)
    {
        var ownTx = outerTx is null;
        var tx = outerTx ?? conn.BeginTransaction();

        try
        {
            // 1. Snap row (idempotent)
            conn.Execute(
                "INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h, @t)",
                new { h = snapHash, t = DxDatabase.UtcNow() }, tx);

            // 2. File content (streaming, deduplicated)
            foreach (var entry in manifest)
                BlobStore.InsertFile(conn, tx, entry.AbsolutePath);

            // 3. snap_files manifest
            foreach (var entry in manifest)
                conn.Execute(
                    """
                    INSERT OR IGNORE INTO snap_files
                        (session_id, snap_hash, path, content_hash, size_bytes)
                    VALUES (@sid, @sh, @path, @ch, @sz)
                    """,
                    new
                    {
                        sid = sessionId,
                        sh = snapHash,
                        path = entry.Path,
                        ch = entry.ContentHash,
                        sz = entry.Size
                    }, tx);

            // 4. T-handle (optimistic retry inside HandleAssigner)
            var handle = HandleAssigner.AssignHandle(
                conn, tx, sessionId, snapHash, DxDatabase.UtcNow());

            // 5. HEAD upsert
            conn.Execute(
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
