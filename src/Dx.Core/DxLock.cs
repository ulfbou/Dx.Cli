namespace Dx.Core;

/// <summary>
/// Represents an exclusive, process-level advisory lock on the DX workspace, used to
/// prevent concurrent mutations from corrupting the snapshot state.
/// </summary>
/// <remarks>
/// <para>
/// The lock is implemented as an exclusive OS file lock on a dedicated lock file inside
/// the <c>.dx/</c> directory. The default lock file is named <c>snaps.lock</c>, which is
/// deliberately distinct from lock files used by sister tools that may share the same
/// <c>.dx/</c> folder.
/// </para>
/// <para>
/// Callers must dispose the returned <see cref="DxLock"/> instance as soon as the
/// protected region completes. The recommended pattern is a <c>using</c> declaration
/// or statement, which guarantees release even when an exception is thrown.
/// </para>
/// </remarks>
public sealed class DxLock : IDisposable
{
    private readonly FileStream _stream;

    private DxLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Acquires an exclusive file lock on the specified lock file within the workspace
    /// <c>.dx/</c> directory.
    /// </summary>
    /// <param name="root">The workspace root directory.</param>
    /// <param name="lockName">
    /// The name of the lock file within <c>.dx/</c>.
    /// Defaults to <c>snaps.lock</c>, which scopes the lock to snapshot operations only,
    /// allowing sister tools that use different lock files to operate concurrently.
    /// </param>
    /// <returns>A <see cref="DxLock"/> instance representing the acquired lock.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.PendingTransactionOnOtherSession"/> when the lock
    /// file is already held by another process, indicating a concurrent operation is in
    /// progress.
    /// </exception>
    public static DxLock Acquire(string root, string lockName = "snaps.lock")
    {
        var path = Path.Combine(root, ".dx", lockName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new DxLock(stream);
        }
        catch (IOException)
        {
            throw new DxException(DxError.PendingTransactionOnOtherSession,
                $"Session is locked by another process ({lockName}).");
        }
    }

    /// <summary>
    /// Releases the exclusive file lock by closing and disposing the underlying
    /// <see cref="FileStream"/>.
    /// </summary>
    public void Dispose()
    {
        _stream.Dispose();
    }
}
