using Dapper;

using Dx.Core.Execution;

using Microsoft.Data.Sqlite;

namespace Dx.Core.Protocol;

/// <summary>
/// Orchestrates the atomic execution lifecycle of DX mutations. It serves as the 
/// single authority for the "Guard → Execute → Commit/Rollback" lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// This coordinator enforces crash durability by persisting a singleton record 
/// (ID=1) to the <c>pending_transaction</c> table before any filesystem mutations. 
/// It then opens a <see cref="SqliteTransaction"/> to ensure that all database 
/// side-effects—snapshotting, logging, and clearing the guard—are committed 
/// atomically or not at all.
/// </para>
/// <para>
/// If the process terminates mid-mutation, <see cref="RecoverIfNeededAsync"/> 
/// uses the durable <c>target_hash</c> to restore the working tree to its 
/// last known good state.
/// </para>
/// </remarks>
public sealed class TransactionCoordinator(
    SqliteConnection connection,
    string workspaceRoot,
    IgnoreSet ignoreSet,
    IDxLogger logger)
{
    private readonly SqliteConnection _connection = connection;
    private readonly string _workspaceRoot = workspaceRoot;
    private readonly IgnoreSet _ignoreSet = ignoreSet;
    private readonly IDxLogger _logger = logger;

    /// <summary>
    /// Executes a mutating operation within a durable, transactional guard.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="executeFunc">
    /// The mutation logic. Receives the active <see cref="SqliteTransaction"/> 
    /// which MUST be used for all database writes to preserve atomicity.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A <see cref="DispatchResult"/> indicating the final outcome.</returns>
    /// <exception cref="DxException">
    /// Thrown if another transaction is active or if the lifecycle fails.
    /// </exception>
    public async Task<DispatchResult> RunAsync(
        string sessionId,
        Func<SqliteTransaction, CancellationToken, Task<DispatchResult>> executeFunc,
        CancellationToken ct)
    {
        // Recovery may ONLY occur before we establish a new guard
        await RecoverIfNeededAsync(sessionId, ct);

        var preExecutionHead = await GetCurrentHeadAsync(sessionId, ct);
        await PersistGuardAsync(sessionId, preExecutionHead, ct);

        // Tracks whether *any* filesystem mutation may have occurred
        bool filesystemMutated = false;

        using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        try
        {
            var result = await executeFunc(tx, ct);

            // Successful execution → commit atomically
            await ClearGuardInternalAsync(tx, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DxException dx)
        {
            await tx.RollbackAsync(ct);

            // IMPORTANT:
            // BaseMismatch and similar precondition errors occur BEFORE mutation,
            // so we must not rollback the filesystem in that case.
            if (dx.Error != DxError.BaseMismatch)
            {
                await RollbackFilesystemAsync(preExecutionHead, ct);
            }

            await ClearGuardDurableAsync(sessionId, ct);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Transaction faulted: {ex.Message}");
            await tx.RollbackAsync(ct);
            await RollbackFilesystemAsync(preExecutionHead, ct);
            await ClearGuardDurableAsync(sessionId, ct);
            throw;
        }
    }

    /// <summary>
    /// Detects and repairs interrupted transactions. Must be called within 
    /// an exclusivity lock before starting a new transaction.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when recovery is finished or if no recovery is needed.</returns>
    /// <exception cref="DxException">Thrown if another transaction is active or if recovery fails.</exception>
    public async Task RecoverIfNeededAsync(string sessionId, CancellationToken ct)
    {
        var staleSession = await _connection.QueryFirstOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT session_id FROM pending_transaction WHERE id = 1",
                cancellationToken: ct));

        if (staleSession == null)
            return;

        if (staleSession != sessionId)
        {
            throw new DxException(
                DxError.PendingTransactionOnOtherSession,
                $"The workspace is locked by an interrupted transaction from session '{staleSession}'.");
        }

        _logger.Warn("Recovery: Found stale transaction record. Reverting working tree...");

        var head = await GetCurrentHeadAsync(sessionId, ct);
        await RollbackFilesystemAsync(head, ct);
        await ClearGuardDurableAsync(sessionId, ct);
    }

    private async Task PersistGuardAsync(string sessionId, byte[] hash, CancellationToken ct)
    {
        // Enforce singleton guard (ID=1)
        const string sql = """
            INSERT INTO pending_transaction (id, session_id, started_utc)
            VALUES (1, @sessionId, @now)
            """;

        try
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { sessionId, now = DxDatabase.UtcNow() },
                cancellationToken: ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new DxException(
                DxError.PendingTransactionOnOtherSession,
                "A mutating transaction is already active.");
        }
    }

    private async Task ClearGuardInternalAsync(SqliteTransaction tx, CancellationToken ct) 
        => await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pending_transaction WHERE id = 1", transaction: tx, cancellationToken: ct));

    private async Task ClearGuardDurableAsync(string sessionId, CancellationToken ct) 
        => await _connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pending_transaction WHERE id = 1 AND session_id = @sessionId",
            new { sessionId }, cancellationToken: ct));

    private async Task RollbackFilesystemAsync(byte[] targetHash, CancellationToken ct)
    {
        var engine = new RollbackEngine(_connection, _workspaceRoot, _ignoreSet);
        await Task.Run(() => engine.RestoreTo(targetHash), ct);
    }

    private async Task<byte[]> GetCurrentHeadAsync(string sessionId, CancellationToken ct) 
        => await _connection.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            "SELECT head_snap_hash FROM session_state WHERE session_id = @sid",
            new { sid = sessionId }, cancellationToken: ct))
        ?? throw new DxException(DxError.SessionNotFound, "Session HEAD missing.");
}
