using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

internal sealed record FileManifestRow(string Path, byte[] ContentHash, long SizeBytes);

public sealed class RollbackEngine(SqliteConnection conn, string root, IgnoreSet ignoreSet)
{
    /// <summary>
    /// Restores the working tree to the exact state of snapHash.
    /// Deletes extraneous files, writes missing/changed files, verifies hashes.
    /// </summary>
    public void RestoreTo(byte[] snapHash)
    {
        var target = LoadManifest(snapHash)
            .ToDictionary(e => e.Path, StringComparer.Ordinal);

        var current = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !ignoreSet.IsExcluded(root, f))
            .Select(f => DxPath.Normalize(root, f))
            .ToHashSet(StringComparer.Ordinal);

        // Delete files not in target
        foreach (var path in current.Where(p => !target.ContainsKey(p)))
        {
            File.Delete(DxPath.ToAbsolute(root, path));
            PruneEmptyDirs(Path.GetDirectoryName(DxPath.ToAbsolute(root, path))!);
        }

        // Write missing or changed files
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
            using var src = BlobStore.OpenRead(conn, entry.ContentHash);
            // Use File.Create (not OpenWrite) to truncate any existing content first.
            // OpenWrite leaves stale bytes if the new content is shorter than the old,
            // causing hash mismatches on verification.
            using var dst = File.Create(abs);
            src.CopyTo(dst, bufferSize: 81_920);
        }

        // Verify written files
        foreach (var (path, entry) in target)
        {
            var abs = DxPath.ToAbsolute(root, path);
            var actual = DxHash.Sha256File(abs);
            if (!DxHash.Equal(actual, entry.ContentHash))
                throw new DxException(DxError.VerificationFailed,
                    $"Hash mismatch after restore: {path}");
        }
    }

    private IEnumerable<FileManifestRow> LoadManifest(byte[] snapHash)
        => conn.Query<FileManifestRow>(
            """
            SELECT path AS Path, content_hash AS ContentHash, size_bytes AS SizeBytes
            FROM snap_files
            WHERE snap_hash = @sh
            ORDER BY path ASC
            """,
            new { sh = snapHash });

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
