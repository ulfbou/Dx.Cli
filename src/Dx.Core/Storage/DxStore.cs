using Dapper;

using Microsoft.Data.Sqlite;

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Dx.Core.Storage;

/// <summary>
/// Provides access to a session log backed by a SQLite database, enabling persistent storage and retrieval of canonical
/// I/O records for workspace history.
/// </summary>
/// <remarks>The session log serves as the authoritative record of workspace activity, with each entry timestamped
/// by the database to ensure a reliable and monotonic audit trail. This class is not thread-safe; callers should ensure
/// appropriate synchronization if accessed concurrently.</remarks>
/// <param name="connection">The open SQLite connection used to interact with the session log database. Cannot be null.</param>
public partial class DxStore(
    SqliteConnection connection,
    string sessionId) : IDxStore
{
    private readonly SqliteConnection _connection = connection;
    private readonly string _sessionId = sessionId;

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
                session_id,
                direction,
                document,
                tx_success,
                snap_handle,
                created_at
            )
            VALUES (
                @SessionId,
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
                SessionId = _sessionId,
                entry.Direction,
                entry.Document,
                entry.TxSuccess,
                entry.SnapHandle
            },
            cancellationToken: ct));
    }
}
