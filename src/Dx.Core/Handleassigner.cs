using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

public sealed record SnapHandleRow(string Handle, byte[] SnapHash, int Seq, string CreatedUtc);

public static class HandleAssigner
{
    private const int MaxRetries = 10;

    /// <summary>
    /// Assigns a T-handle to a snap within a session.
    /// Idempotent: if this snap already has a handle in this session, returns the existing one.
    /// Retries on seq collision (UNIQUE session_id+handle) up to MaxRetries times.
    /// </summary>
    public static string AssignHandle(
        SqliteConnection conn,
        SqliteTransaction tx,
        string sessionId,
        byte[] snapHash,
        string createdUtc)
    {
        // Fast path: snap already has a handle in this session (e.g. checkout of existing snap)
        var existing = ReverseResolve(conn, sessionId, snapHash);
        if (existing is not null)
            return existing;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var nextSeq = conn.ExecuteScalar<int>(
                "SELECT COALESCE(MAX(seq) + 1, 0) FROM snap_handles WHERE session_id = @sessionId",
                new { sessionId }, tx);

            var handle = $"T{nextSeq:D4}";

            try
            {
                conn.Execute(
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

    public static byte[]? Resolve(SqliteConnection conn, string sessionId, string handle)
        => conn.QuerySingleOrDefault<byte[]>(
            "SELECT snap_hash FROM snap_handles WHERE session_id = @sessionId AND handle = @handle",
            new { sessionId, handle });

    public static string? ReverseResolve(SqliteConnection conn, string sessionId, byte[] snapHash)
        => conn.QuerySingleOrDefault<string>(
            "SELECT handle FROM snap_handles WHERE session_id = @sessionId AND snap_hash = @snapHash",
            new { sessionId, snapHash });

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
