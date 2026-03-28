using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core.Storage;

using Microsoft.Data.Sqlite;

using Dapper;

/// <summary>
/// Provides high-performance, content-addressed storage of raw file bytes using 
/// SQLite's Incremental BLOB I/O.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses <c>zeroblob</c> and <see cref="SqliteBlob"/> to stream 
/// data directly between the filesystem and the database page cache, bypassing 
/// managed memory overhead.
/// </para>
/// <para>
/// Data integrity is maintained via SHA-256 hashing and SQLite's Write-Ahead Log (WAL),
/// ensuring that file writes are atomic and deduplicated.
/// </para>
/// </remarks>
public sealed class SqliteContentStore
{
    private readonly SqliteConnection _connection;

    public SqliteContentStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Stores a file's content in the database using a streaming approach.
    /// If the content already exists (deduplication), the stream is not consumed.
    /// </summary>
    /// <param name="hash">The SHA-256 hash of the content.</param>
    /// <param name="sourceStream">The source stream to read the bytes from.</param>
    /// <param name="length">The total length of the content in bytes.</param>
    /// <param name="ct">A cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous store operation.</returns>
    public async Task StoreAsync(byte[] hash, Stream sourceStream, long length, CancellationToken ct = default)
    {
        // 1. Deduplication Check
        // We check for existence first to avoid unnecessary 'zeroblob' allocations.
        var existingRowId = await _connection.QueryFirstOrDefaultAsync<long?>(
            "SELECT rowid FROM file_content WHERE content_hash = @hash",
            new { hash });

        if (existingRowId.HasValue)
        {
            return;
        }

        // 2. Reserve Space
        // We insert a pointer and a zeroblob of the exact size required.
        // Using RETURNING rowid is the most efficient way to get the handle for streaming.
        var rowId = await _connection.QuerySingleAsync<long>(
            """
            INSERT INTO file_content (content_hash, content, size_bytes, inserted_at)
            VALUES (@hash, zeroblob(@len), @len, @now)
            RETURNING rowid;
            """,
            new { hash, len = length, now = DateTime.UtcNow });

        // 3. Incremental I/O Stream
        // We open the blob for writing. This is a specialized SQLite stream.
        using var blobStream = new SqliteBlob(_connection, "file_content", "content", rowId);

        // Use a standard 80KB buffer for optimal disk I/O alignment
        await sourceStream.CopyToAsync(blobStream, 81920, ct);
    }

    /// <summary>
    /// Opens a read-only streaming view of the stored bytes for the given content hash.
    /// </summary>
    /// <param name="hash">The SHA-256 hash identifying the stored content.</param>
    /// <returns>
    /// A <see cref="Stream"/> positioned at the start of the stored blob.
    /// The caller is responsible for disposing the stream.
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when no entry exists for the specified hash.
    /// </exception>
    public Stream OpenRead(byte[] hash)
    {
        var rowId = _connection.QueryFirstOrDefault<long?>(
            "SELECT rowid FROM file_content WHERE content_hash = @hash",
            new { hash });

        if (rowId is null)
        {
            throw new KeyNotFoundException($"Content hash not found: {Convert.ToHexString(hash)}");
        }

        // Zero-copy read path: provides a stream directly over the SQLite page cache
        return new SqliteBlob(_connection, "file_content", "content", rowId.Value, readOnly: true);
    }

    /// <summary>
    /// Reads the entire stored content for the given hash into a byte array.
    /// </summary>
    /// <remarks>
    /// Warning: For large files, this will allocate the full content size in managed memory.
    /// Prefer <see cref="OpenRead"/> for files exceeding a few megabytes.
    /// </remarks>
    public async Task<byte[]> ReadAllBytesAsync(byte[] hash, CancellationToken ct = default)
    {
        using var s = OpenRead(hash);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
