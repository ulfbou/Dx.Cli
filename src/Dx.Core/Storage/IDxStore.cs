namespace Dx.Core.Storage;

using System.Threading;
using System.Threading.Tasks;

public partial interface IDxStore
{
    /// <summary>
    /// Appends a new entry to the <c>session_log</c> table.
    /// </summary>
    /// <param name="entry">The log entry data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="entry"/> is <see langword="null"/>.</exception>
    Task WriteSessionLogAsync(SessionLogEntry entry, CancellationToken ct = default);
}
