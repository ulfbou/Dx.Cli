using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

public static class BlobStore
{
    /// <summary>
    /// Inserts file content via streaming. No-ops if already present (deduplication).
    /// Returns the content hash.
    /// </summary>
    public static byte[] InsertFile(
        SqliteConnection conn,
        SqliteTransaction tx,
        string absolutePath)
    {
        var contentHash = DxHash.Sha256File(absolutePath);
        var sizeBytes = new FileInfo(absolutePath).Length;

        // Deduplication fast path
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM file_content WHERE content_hash = @hash",
            new { hash = contentHash }, tx);

        if (exists > 0) return contentHash;

        // Insert placeholder then stream into blob
        conn.Execute(
            """
            INSERT INTO file_content (content_hash, content, size_bytes, inserted_at)
            VALUES (@hash, zeroblob(@size), @size, @now)
            """,
            new { hash = contentHash, size = sizeBytes, now = DxDatabase.UtcNow() }, tx);

        var rowId = conn.ExecuteScalar<long>(
            "SELECT rowid FROM file_content WHERE content_hash = @hash",
            new { hash = contentHash }, tx);

        using var writeStream = new SqliteBlob(conn, "file_content", "content", rowId);
        using var readStream = File.OpenRead(absolutePath);
        readStream.CopyTo(writeStream, bufferSize: 81_920);

        return contentHash;
    }

    public static Stream OpenRead(SqliteConnection conn, byte[] contentHash)
    {
        var rowId = conn.ExecuteScalar<long?>(
            "SELECT rowid FROM file_content WHERE content_hash = @hash",
            new { hash = contentHash });

        if (rowId is null)
            throw new DxException(DxError.DatabaseCorruption,
                $"Content hash not found: {DxHash.ToHex(contentHash)}");

        return new SqliteBlob(conn, "file_content", "content", rowId.Value, readOnly: true);
    }

    public static byte[] ReadAllBytes(SqliteConnection conn, byte[] contentHash)
    {
        using var s = OpenRead(conn, contentHash);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
