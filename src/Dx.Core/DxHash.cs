using System.Security.Cryptography;

namespace Dx.Core;

/// <summary>
/// Provides SHA-256 hashing utilities used throughout DX to identify and deduplicate
/// file content and to compute deterministic snapshot hashes.
/// </summary>
/// <remarks>
/// All hash operations produce raw 32-byte arrays. Hex string conversion is available
/// via <see cref="ToHex"/> and <see cref="FromHex"/> for display and persistence purposes.
/// </remarks>
public static class DxHash
{
    /// <summary>
    /// Computes the SHA-256 hash of the contents of a file on disk.
    /// The file is read as a stream so that large files are not fully buffered in memory.
    /// </summary>
    /// <param name="path">The absolute path of the file to hash.</param>
    /// <returns>A 32-byte array containing the SHA-256 digest of the file content.</returns>
    public static byte[] Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a span of raw bytes.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <returns>A 32-byte array containing the SHA-256 digest.</returns>
    public static byte[] Sha256Bytes(ReadOnlySpan<byte> data)
        => SHA256.HashData(data);

    /// <summary>
    /// Converts a raw SHA-256 hash byte array to a lowercase hexadecimal string.
    /// </summary>
    /// <param name="hash">The 32-byte hash array to convert.</param>
    /// <returns>A 64-character lowercase hexadecimal string.</returns>
    public static string ToHex(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>
    /// Converts a hexadecimal string back to a raw byte array.
    /// </summary>
    /// <param name="hex">A hexadecimal string of even length (case-insensitive).</param>
    /// <returns>The decoded byte array.</returns>
    public static byte[] FromHex(string hex)
        => Convert.FromHexString(hex);

    /// <summary>
    /// Performs a constant-time byte-sequence equality check between two hash arrays.
    /// </summary>
    /// <param name="a">The first hash to compare.</param>
    /// <param name="b">The second hash to compare.</param>
    /// <returns>
    /// <see langword="true"/> when both arrays contain the same bytes in the same order;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool Equal(byte[] a, byte[] b)
        => a.AsSpan().SequenceEqual(b);
}
