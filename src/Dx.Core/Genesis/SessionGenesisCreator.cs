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
    /// <summary>
    /// Creates a new session with a genesis snapshot (T0000) based on the current state of the filesystem.
    /// The process includes validating the uniqueness of the session ID, building an ignore set, generating 
    /// a manifest of files, computing a snap hash, and inserting all relevant data into the database within 
    /// a single transaction to ensure atomicity.
    /// </summary>
    /// <param name="conn">An open SqliteConnection to the database where the session should be created.</param>
    /// <param name="root">The root directory of the session, which will be scanned to create the genesis snapshot.</param>
    /// <param name="sessionId">The unique identifier for the session being created. Must not already exist in the database.</param>
    /// <param name="artifactsDir">An optional directory path that should be treated as containing build artifacts, which may be excluded from the snapshot.</param>
    /// <param name="excludes">An optional collection of paths or patterns to exclude from the genesis snapshot.</param>
    /// <param name="includeBuildOutput">A flag indicating whether build output should be included in the genesis snapshot.</param>
    /// <returns>A result object containing information about the created session and its genesis snapshot.</returns>
    /// <exception cref="DxException">Thrown if the session ID already exists or if any other error occurs during creation.</exception>
    public static SessionGenesisResult Create(
        SqliteConnection conn,
        string root,
        string sessionId,
        string? artifactsDir,
        IEnumerable<string>? excludes,
        bool includeBuildOutput)
    {
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sessions WHERE session_id=@sid",
            new { sid = sessionId });

        if (exists > 0)
            throw new DxException(DxError.InvalidArgument,
                $"Session already exists: {sessionId}");

        var ignoreSet = IgnoreSetFactory.Create(
            artifactsDir, excludes ?? [], includeBuildOutput);

        var now = DxDatabase.UtcNow();

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
                    sid = sessionId,
                    root = Path.GetFullPath(root),
                    arts = artifactsDir,
                    ign = ignoreSet.Serialize(),
                    t = now
                }, tx);

            var manifest = ManifestBuilder.Build(root, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            conn.Execute(
                "INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h,@t)",
                new { h = snapHash, t = now }, tx);

            foreach (var e in manifest)
                BlobStore.InsertFile(conn, tx, e.AbsolutePath);

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

            var handle = HandleAssigner.AssignHandle(
                conn, tx, sessionId, snapHash, now);

            conn.Execute(
                """
                INSERT INTO session_state (session_id, head_snap_hash, updated_utc)
                VALUES (@sid, @sh, @t)
                """,
                new { sid = sessionId, sh = snapHash, t = now }, tx);

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
