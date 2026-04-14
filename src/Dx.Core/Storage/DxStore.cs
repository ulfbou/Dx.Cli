using Dapper;

using System.Threading;
using System.Threading.Tasks;

namespace Dx.Core.Storage;

public partial class DxStore : IDxStore
{
    /// <summary>
    /// Persists a canonical I/O record to the SQLite-backed session log.
    /// </summary>
    /// <param name="entry">The structured log entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing completion.</returns>
    /// <remarks>
    /// The <c>session_log</c> is the authoritative source of truth for workspace history.
    /// Every entry is timestamped by the database to ensure a monotonic audit trail.
    /// </remarks>
    public async Task WriteSessionLogAsync(SessionLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        const string sql = @"
            INSERT INTO session_log (
                direction, 
                document, 
                tx_success, 
                snap_handle, 
                created_at
            ) 
            VALUES (
                @Direction, 
                @Document, 
                @TxSuccess, 
                @SnapHandle, 
                STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW')
            );";

        await _connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                entry.Direction,
                entry.Document,
                entry.TxSuccess,
                entry.SnapHandle
            },
            cancellationToken: ct));
    }
}
