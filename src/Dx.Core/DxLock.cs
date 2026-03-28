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
    private readonly FileStream _lockHandle;

    private DxLock(FileStream stream) => _lockHandle = stream;

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
    public static async Task<DxLock> AcquireAsync(string lockFilePath, TimeSpan timeout, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var linkedToken = timeoutCts.Token;

        var rng = new Random(Guid.NewGuid().GetHashCode());
        int attempt = 0;
        int baseDelayMs = 20;

        await Task.Delay(rng.Next(5, 15), linkedToken);

        while (true)
        {
            try
            {
                linkedToken.ThrowIfCancellationRequested();

                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose
                );

                return new DxLock(stream);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                // The kernel confirms another process holds the lock. We wait.
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // If the failure was our timeout (not the user hitting Ctrl+C), throw a clear diagnostic error.
                throw new TimeoutException(
                    $"Failed to acquire exclusive workspace lock at '{lockFilePath}' within {timeout.TotalSeconds} seconds. " +
                    $"Another 'dx' process is currently mutating the directory."
                );
            }

            attempt++;
            int maxDelay = Math.Min(baseDelayMs * (int)Math.Pow(2, attempt), 500);
            await Task.Delay(rng.Next(baseDelayMs, maxDelay), linkedToken);
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;

        if (OperatingSystem.IsWindows())
        {
            return code == 32; // ERROR_SHARING_VIOLATION
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // 11 = EAGAIN, 13 = EACCES, 35 = EDEADLK (macOS)
            return code is 11 or 13 or 35;
        }

        return true;
    }

    /// <summary>
    /// Releases the exclusive file lock by closing and disposing the underlying
    /// <see cref="FileStream"/>.
    /// </summary>
    public void Dispose() => _lockHandle.Dispose();

    /// <summary>
    /// Releases the exclusive file lock by closing and disposing the underlying
    /// <see cref="FileStream"/>.
    /// </summary>
    public async ValueTask DisposeAsync() => await _lockHandle.DisposeAsync();
}
