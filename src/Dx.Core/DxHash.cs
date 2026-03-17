using System.Security.Cryptography;

namespace Dx.Core;

public static class DxHash
{
    public static byte[] Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    public static byte[] Sha256Bytes(ReadOnlySpan<byte> data)
        => SHA256.HashData(data);

    public static string ToHex(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();

    public static byte[] FromHex(string hex)
        => Convert.FromHexString(hex);

    public static bool Equal(byte[] a, byte[] b)
        => a.AsSpan().SequenceEqual(b);
}
