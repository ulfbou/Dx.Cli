using Microsoft.Data.Sqlite;

namespace Dx.Core.Execution;

/// <summary>
/// Carries the per-invocation state an <see cref="IBlockExecutor"/> needs
/// to execute a single block without reaching back into the dispatcher.
/// </summary>
internal sealed record BlockExecutionContext(
    string WorkspaceRoot,
    string SessionId,
    IgnoreSet IgnoreSet,
    bool IsDryRun,
    SqliteConnection Connection,
    IDxLogger Logger,
    int? RunTimeoutSeconds);
