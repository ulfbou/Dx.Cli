using Dapper;

using Dx.Core;
using Dx.Core.Protocol;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

namespace Dx.Cli.Infrastructure;

/// <summary>
/// CLI-only wiring helper.
/// Resolves workspace/session state and constructs a Dispatcher.
/// Contains NO execution logic.
/// </summary>
internal sealed class CliDispatcherContext
{
    public DxDispatcher Dispatcher { get; }
    public string WorkspaceRoot { get; }
    public string SessionId { get; }
    public IgnoreSet IgnoreSet { get; }
    public SqliteConnection Connection { get; }

    private CliDispatcherContext(
        DxDispatcher dispatcher,
        SqliteConnection connection,
        string workspaceRoot,
        string sessionId,
        IgnoreSet ignoreSet)
    {
        Dispatcher = dispatcher;
        Connection = connection;
        WorkspaceRoot = workspaceRoot;
        SessionId = sessionId;
        IgnoreSet = ignoreSet;
    }


    public static async Task<CliDispatcherContext> OpenAsync(
        string workspaceRoot,
        string? sessionId,
        IDxLogger? logger,
        CancellationToken ct)
    {
        if (!DxRuntime.IsWorkspace(workspaceRoot))
        {
            throw new DxException(
                DxError.WorkspaceNotInitialized,
                $"No DX workspace found at {workspaceRoot}");
        }

        var connection = await DxDatabase.OpenAsync(workspaceRoot, ct: ct);
        await DxDatabase.MigrateAsync(connection, ct);

        sessionId ??= await connection.ExecuteScalarAsync<string>(
            "SELECT session_id FROM sessions WHERE closed_utc IS NULL ORDER BY created_utc DESC LIMIT 1", ct)
            ?? throw new DxException(DxError.SessionNotFound, "No active session");

        var ignoreJson = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition(
                "SELECT ignore_set_json FROM sessions WHERE session_id = @sid",
                new { sid = sessionId },
                cancellationToken: ct))
            ?? throw new DxException(DxError.SessionNotFound, $"Session not found: {sessionId}");


        var ignoreSet = IgnoreSet.Deserialize(ignoreJson);
        var store = new DxStore(connection, sessionId);

        var dispatcher = new DxDispatcher(
            connection,
            store,
            workspaceRoot,
            ignoreSet,
            sessionId,
            logger);

        return new CliDispatcherContext(
            dispatcher,
            connection,
            workspaceRoot,
            sessionId,
            ignoreSet);
    }
}