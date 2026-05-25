using Microsoft.Data.Sqlite;

namespace Dx.Core.Execution;

/// <summary>
/// Carries the per-invocation state an <see cref="IBlockExecutor"/> needs
/// to execute a single block without reaching back into the dispatcher.
/// </summary>
/// <param name="WorkspaceRoot">The root directory of the workspace where execution is occurring. All file paths should be resolved against this.</param>
/// <param name="SessionId">The unique identifier for the current execution session, used for
/// accountability and logging correlation.</param>
/// <param name="IgnoreSet">The set of file paths to ignore during execution, used
/// to prevent operations on files that are outside the scope of the current execution.</param>
/// <param name="IsDryRun">Indicates whether the current execution is a dry run
/// (i.e., no state mutations should be performed, and all operations should be logged as if they were executed without actually performing them).</param>
/// <param name="Connection">The SQLite connection to use for any database operations during execution.
/// Executors should not manage the lifecycle of this connection (e.g., opening, closing) as it is owned by the dispatcher.</param>
/// <param name="Logger">The logger to use for any logging during execution. Executors should
/// use this for all logging to ensure that log messages are properly correlated with the current execution session.</param>
/// <param name="RunTimeoutSeconds">An optional timeout for the execution run, in seconds
/// (used to enforce time limits on long-running operations; if null, no timeout is applied).</param>
/// <param name="Dispatcher">An optional reference to the dispatcher, which can be used for
/// advanced scenarios where the executor needs to interact with the dispatcher (e.g., to enqueue additional blocks for execution). Executors should use this reference with caution to avoid tight coupling with the dispatcher.</param>
/// <param name="Transaction">An optional SQLite transaction that the executor can use for database operations
/// (used to ensure atomicity of database operations across multiple blocks; if null, the executor can perform operations without a transaction, but should be aware that they may not be atomic with respect to other blocks).</param>
/// <param name="CancellationToken">A cancellation token that the executor should observe to support cooperative cancellation of long-running operations.</param>
/// <remarks>
/// <para>
/// This record is designed to encapsulate all the contextual information an <see cref="IBlockExecutor"/> might need to execute a block, without requiring the executor to reach back into the dispatcher for additional information. This promotes separation of concerns and makes executors more self-contained and easier to test.
/// </para>
/// <para>
/// The properties of this record are intended to cover a wide range of execution scenarios, from simple file operations to complex interactions with the dispatcher and database. Executors can choose which properties to utilize based on
/// their specific needs, but all properties are provided to ensure that executors have the necessary context to perform their tasks effectively.
/// </para>
/// <para>
/// Executors should be designed to be resilient and should use the provided context to handle various execution scenarios
/// (e.g., respecting the ignore set, handling dry run mode, using the logger for all logging, observing cancellation, etc.) to ensure that they behave correctly within the larger execution framework.
/// </para>
/// </remarks>
public sealed record BlockExecutionContext(
    string WorkspaceRoot,
    string SessionId,
    IgnoreSet IgnoreSet,
    bool IsDryRun,
    SqliteConnection Connection,
    IDxLogger Logger,
    int? RunTimeoutSeconds,
    Dx.Core.Protocol.DxDispatcher? Dispatcher,
    SqliteTransaction? Transaction,
    CancellationToken CancellationToken);
