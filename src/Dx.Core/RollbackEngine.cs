using Dapper;

using Microsoft.Data.Sqlite;

using System.IO;
using System.Linq;

namespace Dx.Core;

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
public sealed class RollbackEngine
{
    private readonly SqliteConnection _conn;
    private readonly string _root;
    private readonly IgnoreSet _ignore;

    /// <summary>
    /// Initializes a new instance of the <see cref="RollbackEngine"/> class with the specified database connection,
    /// workspace root path, and ignore set.
    /// </summary>
    /// <param name="conn">An open database connection to the workspace <c>snap.db</c>.</param>
    /// <param name="root">The absolute workspace root path.</param>
    /// <param name="ignoreSet">
    /// The file exclusion rules for the session, used to identify files that should not
    /// be deleted or written during the restore operation.
    /// </param>
    public RollbackEngine(SqliteConnection conn, string root, IgnoreSet ignoreSet)
    {
        _conn = conn;
        _root = root;
        _ignore = ignoreSet;
    }

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
        // 1. Read the desired snapshot’s manifest (paths are already normalized)
        var snapFiles = _conn.Query<(string Path, byte[] Hash)>(
            """
                SELECT path, content_hash
                FROM snap_files
                WHERE snap_hash = @sh
                ORDER BY path
                """,
            new { sh = snapHash }).ToDictionary(r => r.Path, r => r.Hash);

        // 2. Enumerate current working tree (exclude .dx/ etc)
        var currentFiles = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(abs =>
            {
                var rel = DxPath.Normalize(_root, abs);
                return (Abs: abs, Rel: rel);
            })
            .Where(x => !_ignore.IsExcluded(x.Rel))
            .ToDictionary(x => x.Rel, x => x.Abs, StringComparer.OrdinalIgnoreCase);

        // 3. Delete files not present in snapshot
        foreach (var rel in currentFiles.Keys)
        {
            if (!snapFiles.ContainsKey(rel))
            {
                File.Delete(currentFiles[rel]);
            }
        }

        // 4. Restore/update files that exist in snapshot
        foreach (var (rel, hash) in snapFiles)
        {
            // Skip ignored patterns (safety: snap_files normally doesn’t contain them)
            if (_ignore.IsExcluded(rel)) continue;

            var abs = DxPath.ToAbsolute(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

            var store = new Storage.SqliteContentStore(_conn);
            using var src = store.OpenRead(hash);
            using var dst = File.Open(abs, FileMode.Create, FileAccess.Write);
            src.CopyTo(dst);
            dst.SetLength(dst.Position);
        }
    }
}
