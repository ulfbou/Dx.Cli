using System.Security.Cryptography;
using System.Text;

namespace Dx.Core;

/// <summary>
/// Represents a single file entry in a workspace snapshot manifest.
/// </summary>
/// <param name="Path">
/// The normalised, forward-slash-separated relative path of the file within the workspace root.
/// </param>
/// <param name="AbsolutePath">
/// The OS-specific absolute path of the file, used for streaming blob reads during snapshot creation.
/// </param>
/// <param name="ContentHash">The SHA-256 digest of the raw file bytes.</param>
/// <param name="Size">The raw byte size of the file.</param>
public sealed record ManifestEntry(
    string Path,
    string AbsolutePath,
    byte[] ContentHash,
    long Size
);

/// <summary>
/// Builds the file manifest for a workspace snapshot by enumerating the working tree
/// and computing a deterministic, content-addressed snapshot hash.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is always sorted by normalised path using ordinal comparison, ensuring
/// that the snapshot hash is deterministic regardless of filesystem enumeration order.
/// </para>
/// <para>
/// Files matched by the session's <see cref="IgnoreSet"/> are excluded from both the
/// manifest and the snapshot hash computation.
/// </para>
/// </remarks>
public static class ManifestBuilder
{
    /// <summary>
    /// Enumerates all non-excluded files in the workspace and builds the snapshot manifest.
    /// </summary>
    /// <param name="root">The absolute workspace root path to enumerate.</param>
    /// <param name="ignoreSet">
    /// The session's file exclusion rules. Files whose normalised paths match any exclusion
    /// prefix are omitted from the manifest.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="ManifestEntry"/> records ordered by normalised path.
    /// </returns>
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
    /// Computes a deterministic SHA-256 hash over the entire manifest, producing a unique
    /// fingerprint for the snapshot that changes whenever any tracked file is added,
    /// removed, or modified.
    /// </summary>
    /// <param name="entries">
    /// The manifest entries to hash, which must already be sorted by path.
    /// </param>
    /// <returns>
    /// A 32-byte SHA-256 digest computed by incrementally hashing each entry's
    /// null-terminated UTF-8 path followed by its content hash.
    /// </returns>
    public static byte[] ComputeSnapHash(IReadOnlyList<ManifestEntry> entries)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var e in entries)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(e.Path));
            hasher.AppendData([0x00]); // null separator ensures path boundaries are distinct
            hasher.AppendData(e.ContentHash);
        }

        return hasher.GetHashAndReset();
    }
}
