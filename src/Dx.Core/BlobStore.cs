using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// Provides content-addressed storage of raw file bytes inside the workspace SQLite database.
/// </summary>
/// <remarks>
/// <para>
/// Files are keyed by their SHA-256 content hash, so identical files are stored only once
/// regardless of how many snapshots reference them (deduplication).
/// </para>
/// <para>
/// Large files are written to the database using the SQLite incremental blob I/O API
/// (<see cref="SqliteBlob"/>) to avoid materialising the full content in managed memory.
/// </para>
/// </remarks>
public static class BlobStore
{
    /// <summary>
    /// Stores the raw bytes of a file in the <c>file_content</c> table, keyed by its
    /// SHA-256 hash. If an entry for the same hash already exists the method returns
    /// immediately without performing any I/O (content-addressed deduplication).
    /// </summary>
    /// <param name="conn">An open database connection to write to.</param>
    /// <param name="tx">The enclosing transaction; all writes are performed within it.</param>
    /// <param name="absolutePath">The absolute filesystem path of the file to store.</param>
    /// <returns>The SHA-256 content hash of the stored (or already-present) file.</returns>
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

        // Insert a zeroblob placeholder then stream actual bytes into it
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

    /// <summary>
    /// Opens a read-only streaming view of the stored bytes for the given content hash.
    /// </summary>
    /// <param name="conn">An open database connection to read from.</param>
    /// <param name="contentHash">The SHA-256 hash identifying the stored content.</param>
    /// <returns>
    /// A <see cref="Stream"/> positioned at the start of the stored blob.
    /// The caller is responsible for disposing the stream.
    /// </returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.DatabaseCorruption"/> when no entry exists for
    /// the specified hash, which indicates a referential integrity violation.
    /// </exception>
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

    /// <summary>
    /// Reads the entire stored content for the given hash into a byte array.
    /// For large files prefer <see cref="OpenRead"/> to avoid allocating the full buffer.
    /// </summary>
    /// <param name="conn">An open database connection to read from.</param>
    /// <param name="contentHash">The SHA-256 hash identifying the stored content.</param>
    /// <returns>A byte array containing the complete stored file content.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.DatabaseCorruption"/> when no entry exists for
    /// the specified hash.
    /// </exception>
    public static byte[] ReadAllBytes(SqliteConnection conn, byte[] contentHash)
    {
        using var s = OpenRead(conn, contentHash);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
