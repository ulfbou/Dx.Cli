using System.Security.Cryptography;
using System.Text;

namespace Dx.Core;

public sealed record ManifestEntry(
    string Path,         // normalized, relative, forward-slash
    string AbsolutePath, // absolute OS path for streaming reads
    byte[] ContentHash,  // SHA-256 of raw bytes
    long Size          // raw byte count
);

public static class ManifestBuilder
{
    public static IReadOnlyList<ManifestEntry> Build(string root, IgnoreSet ignoreSet)
    {
        var list = new List<ManifestEntry>();

        foreach (var file in Directory.EnumerateFiles(
            root, "*", SearchOption.AllDirectories))
        {
            if (ignoreSet.IsExcluded(root, file)) continue;

            var hash = DxHash.Sha256File(file);
            var norm = DxPath.Normalize(root, file);
            var size = new FileInfo(file).Length;

            list.Add(new ManifestEntry(norm, file, hash, size));
        }

        return [.. list.OrderBy(e => e.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Compute the snap content hash from a sorted manifest.
    /// Algorithm: SHA-256 over (path_utf8 + NUL + content_hash) for each entry.
    /// </summary>
    public static byte[] ComputeSnapHash(IReadOnlyList<ManifestEntry> entries)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var e in entries)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(e.Path));
            hasher.AppendData([0x00]);
            hasher.AppendData(e.ContentHash);
        }

        return hasher.GetHashAndReset();
    }
}
