using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// Internal Dapper mapping record for file manifest rows retrieved during a restore operation.
/// </summary>
/// <param name="Path">The normalised, forward-slash-separated relative path of the file.</param>
/// <param name="ContentHash">The SHA-256 content hash of the file as stored in the blob store.</param>
/// <param name="SizeBytes">The raw byte size of the file at snapshot time.</param>
internal sealed record FileManifestRow(string Path, byte[] ContentHash, long SizeBytes);

/// <summary>
/// Restores the workspace working tree to the exact state recorded in a specified snapshot,
/// removing files that do not belong and writing or overwriting files whose content has changed.
/// </summary>
/// <remarks>
/// <para>
/// The engine operates in three passes:
/// <list type="number">
///   <item><description>
///     Delete working-tree files that are absent from the target manifest and prune any
///     resulting empty directories.
///   </description></item>
///   <item><description>
///     Write or overwrite files whose content hash differs from the stored blob.
///     Files whose hash already matches are left untouched to minimise filesystem I/O.
///   </description></item>
///   <item><description>
///     Verify every file in the target manifest by re-hashing and comparing against the
///     stored content hash. A mismatch raises <see cref="DxError.VerificationFailed"/>.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// This class is used by both <see cref="DxRuntime.Checkout"/> and the crash-recovery
/// path in <see cref="Protocol.DxDispatcher"/>.
/// </para>
/// </remarks>
/// <param name="conn">An open database connection to the workspace <c>snap.db</c>.</param>
/// <param name="root">The absolute workspace root path.</param>
/// <param name="ignoreSet">
/// The file exclusion rules for the session, used to identify files that should not
/// be deleted or written during the restore operation.
/// </param>
public sealed class RollbackEngine(SqliteConnection conn, string root, IgnoreSet ignoreSet)
{
    /// <summary>
    /// Restores the working tree to the state described by the snapshot identified by
    /// <paramref name="snapHash"/>.
    /// </summary>
    /// <param name="snapHash">
    /// The raw 32-byte SHA-256 hash of the target snapshot, as stored in <c>snap_handles</c>.
    /// </param>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.VerificationFailed"/> when the SHA-256 hash of a
    /// restored file does not match the stored blob, indicating storage corruption or a
    /// race condition with another process modifying the working tree.
    /// </exception>
    public void RestoreTo(byte[] snapHash)
    {
        var target = LoadManifest(snapHash)
            .ToDictionary(e => e.Path, StringComparer.Ordinal);

        var current = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !ignoreSet.IsExcluded(root, f))
            .Select(f => DxPath.Normalize(root, f))
            .ToHashSet(StringComparer.Ordinal);

        // Pass 1: delete files not in target
        foreach (var path in current.Where(p => !target.ContainsKey(p)))
        {
            File.Delete(DxPath.ToAbsolute(root, path));
            PruneEmptyDirs(Path.GetDirectoryName(DxPath.ToAbsolute(root, path))!);
        }

        // Pass 2: write missing or changed files
        foreach (var (path, entry) in target)
        {
            var abs = DxPath.ToAbsolute(root, path);
            var needsWrite = true;

            if (File.Exists(abs))
            {
                var existing = DxHash.Sha256File(abs);
                needsWrite = !DxHash.Equal(existing, entry.ContentHash);
            }

            if (!needsWrite) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

            // Use File.Create (not OpenWrite) to truncate any existing content.
            // OpenWrite leaves stale bytes if the new content is shorter than the old,
            // causing hash mismatches on verification.
            using var src = BlobStore.OpenRead(conn, entry.ContentHash);
            using var dst = File.Create(abs);
            src.CopyTo(dst, bufferSize: 81_920);
        }

        // Pass 3: verify restored files
        foreach (var (path, entry) in target)
        {
            var abs = DxPath.ToAbsolute(root, path);
            var actual = DxHash.Sha256File(abs);
            if (!DxHash.Equal(actual, entry.ContentHash))
                throw new DxException(DxError.VerificationFailed,
                    $"Hash mismatch after restore: {path}");
        }
    }

    /// <summary>
    /// Loads the file manifest for the given snapshot hash from the database.
    /// </summary>
    /// <param name="snapHash">The raw 32-byte SHA-256 hash of the snapshot.</param>
    /// <returns>An enumerable sequence of <see cref="FileManifestRow"/> records.</returns>
    private IEnumerable<FileManifestRow> LoadManifest(byte[] snapHash)
        => conn.Query<FileManifestRow>(
            """
            SELECT path AS Path, content_hash AS ContentHash, size_bytes AS SizeBytes
            FROM snap_files
            WHERE snap_hash = @sh
            ORDER BY path ASC
            """,
            new { sh = snapHash });

    /// <summary>
    /// Walks up the directory tree from <paramref name="dir"/>, deleting each empty
    /// directory until a non-empty ancestor or the workspace root is reached.
    /// </summary>
    /// <param name="dir">The starting directory path to prune.</param>
    private void PruneEmptyDirs(string dir)
    {
        while (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }
}
