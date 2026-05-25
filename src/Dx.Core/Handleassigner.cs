using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

public static class HandleAssigner
{
    /// <summary>
    /// Maximum number of handle assignment retries before raising
    /// <see cref="DxError.DatabaseCorruption"/>.
    /// </summary>
    private const int MaxRetries = 10;

    /// <summary>
    /// Assigns the next available handle to a snapshot within a session, or returns the
    /// existing handle if the snapshot is already registered.
    /// </summary>
    /// <param name="connection">An open database connection.</param>
    /// <param name="tx">The enclosing transaction; all writes are performed within it.</param>
    /// <param name="sessionId">The session to assign the handle within.</param>
    /// <param name="snapHash">The 32-byte SHA-256 hash of the snapshot to register.</param>
    /// <param name="createdUtc">The ISO 8601 UTC timestamp to store with the handle row.</param>
    /// <returns>The assigned or pre-existing handle string (e.g. <c>T0004</c>).</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.DatabaseCorruption"/> when a unique handle cannot be
    /// assigned after <see cref="MaxRetries"/> attempts, indicating a high-concurrency conflict
    /// or schema inconsistency.
    /// </exception>
    public static string AssignHandle(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sessionId,
        byte[] snapHash,
        string createdUtc)
    {
        // Fast path: snap already has a handle in this session (e.g. checkout of existing snap)
        var existing = ReverseResolve(connection, sessionId, snapHash);
        if (existing is not null)
            return existing;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var nextSeq = connection.ExecuteScalar<int>(
                "SELECT COALESCE(MAX(seq) + 1, 0) FROM snap_handles WHERE session_id = @sessionId",
                new { sessionId }, tx);

            var handle = $"T{nextSeq:D4}";

            try
            {
                connection.Execute(
                    """
                    INSERT INTO snap_handles (session_id, handle, snap_hash, seq, created_utc)
                    VALUES (@sessionId, @handle, @snapHash, @seq, @createdUtc)
                    """,
                    new { sessionId, handle, snapHash, seq = nextSeq, createdUtc }, tx);

                return handle;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
            {
                // seq collision — retry with fresh MAX(seq)
                continue;
            }
        }

        throw new DxException(DxError.DatabaseCorruption,
            $"Handle assignment failed after {MaxRetries} attempts.");
    }

    /// <summary>
    /// Resolves a handle string to its corresponding raw snapshot hash within a session.
    /// </summary>
    /// <param name="conn">An open database connection.</param>
    /// <param name="sessionId">The session to scope the lookup to.</param>
    /// <param name="handle">The handle string to resolve (e.g. <c>T0002</c>).</param>
    /// <returns>
    /// The 32-byte SHA-256 snapshot hash, or <see langword="null"/> when the handle is
    /// not registered in the session.
    /// </returns>
    public static byte[]? Resolve(SqliteConnection conn, string sessionId, string handle)
        => conn.QuerySingleOrDefault<byte[]>(
            "SELECT snap_hash FROM snap_handles WHERE session_id = @sessionId AND handle = @handle",
            new { sessionId, handle });

    /// <summary>
    /// Resolves a raw snapshot hash to its registered handle string within a session
    /// (the reverse direction of <see cref="Resolve"/>).
    /// </summary>
    /// <param name="conn">An open database connection.</param>
    /// <param name="sessionId">The session to scope the lookup to.</param>
    /// <param name="snapHash">The 32-byte SHA-256 hash to look up.</param>
    /// <returns>
    /// The handle string (e.g. <c>T0001</c>), or <see langword="null"/> when the hash
    /// has no registered handle in the session.
    /// </returns>
    public static string? ReverseResolve(SqliteConnection conn, string sessionId, byte[] snapHash)
        => conn.QuerySingleOrDefault<string>(
            "SELECT handle FROM snap_handles WHERE session_id = @sessionId AND snap_hash = @snapHash",
            new { sessionId, snapHash });

    /// <summary>
    /// Returns all snapshot handle rows for a session in ascending sequence order.
    /// </summary>
    /// <param name="conn">An open database connection.</param>
    /// <param name="sessionId">The session whose handles should be listed.</param>
    /// <returns>An enumerable sequence of <see cref="SnapHandleRow"/> records.</returns>
    public static IEnumerable<SnapHandleRow> ListOrdered(SqliteConnection conn, string sessionId)
        => conn.Query<SnapHandleRow>(
            """
            SELECT handle AS Handle, snap_hash AS SnapHash, seq AS Seq, created_utc AS CreatedUtc
            FROM snap_handles
            WHERE session_id = @sessionId
            ORDER BY seq ASC
            """,
            new { sessionId });
}
