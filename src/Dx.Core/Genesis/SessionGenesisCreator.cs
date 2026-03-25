using Dapper;

using Dx.Core;

using Microsoft.Data.Sqlite;

namespace Dx.Core.Genesis;

/// <summary>
/// Creates a new session + its T0000 snapshot in one atomic database transaction.
/// This is the sole authority for all genesis creation flows.
/// </summary>
public static class SessionGenesisCreator
{
    public static SessionGenesisResult Create(
        SqliteConnection conn,
        string root,
        string sessionId,
        string? artifactsDir,
        IEnumerable<string>? excludes,
        bool includeBuildOutput)
    {
        // 1. Ensure session ID is unique
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sessions WHERE session_id=@sid",
            new { sid = sessionId });

        if (exists > 0)
            throw new DxException(DxError.InvalidArgument,
                $"Session already exists: {sessionId}");

        // 2. Build IgnoreSet (deterministic, unified)
        var ignoreSet = IgnoreSetFactory.Create(
            root, artifactsDir, excludes, includeBuildOutput);

        var now = DxDatabase.UtcNow();

        using var tx = conn.BeginTransaction();
        try
        {
            // 3. Insert session row
            conn.Execute(
                """
                INSERT INTO sessions (session_id, root, artifacts_dir, ignore_set_json, created_utc)
                VALUES (@sid, @root, @arts, @ign, @t)
                """,
                new
                {
                    sid = sessionId,
                    root = Path.GetFullPath(root),
                    arts = artifactsDir,
                    ign = ignoreSet.Serialize(),
                    t = now
                }, tx);

            // 4. Manifest + hash
            var manifest = ManifestBuilder.Build(root, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            // 5. Insert snaps row
            conn.Execute(
                "INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h,@t)",
                new { h = snapHash, t = now }, tx);

            // 6. Insert file_content blobs
            foreach (var e in manifest)
                BlobStore.InsertFile(conn, tx, e.AbsolutePath);

            // 7. Insert snap_files
            foreach (var e in manifest)
            {
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
                        p = e.Path,
                        ch = e.ContentHash,
                        sz = e.Size
                    }, tx);
            }

            // 8. Assign handle T0000
            var handle = HandleAssigner.AssignHandle(
                conn, tx, sessionId, snapHash, now);

            // 9. session_state
            conn.Execute(
                """
                INSERT INTO session_state (session_id, head_snap_hash, updated_utc)
                VALUES (@sid, @sh, @t)
                """,
                new { sid = sessionId, sh = snapHash, t = now }, tx);

            // 10. Genesis log entry
            conn.Execute(
                """
                INSERT INTO session_log
                  (session_id, direction, document, snap_handle, tx_success, created_at)
                VALUES (@sid, 'tool', 'session-genesis', @h, 1, @t)
                """,
                new { sid = sessionId, h = handle, t = now }, tx);

            tx.Commit();

            return new SessionGenesisResult(
                sessionId,
                handle,
                manifest.Count,
                snapHash);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

/// <summary>
/// Returned after creation of a genesis snapshot.
/// </summary>
/// <param name="SessionId">The session ID of the newly created session.</param>
/// <param name="GenesisHandle">The handle assigned to the genesis snapshot (T0000).</param>
/// <param name="FileCount">The number of files included in the genesis snapshot.</param>
/// <param name="SnapHash">The hash of the genesis snapshot.</param>
public sealed record SessionGenesisResult(
    string SessionId,
    string GenesisHandle,
    int FileCount,
    byte[] SnapHash);
