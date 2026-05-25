using Dapper;

using Dx.Core.Protocol;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// Factory for constructing a <see cref="DxRuntime"/> instance, which serves as the primary interface for interacting with a DX workspace.
/// This factory encapsulates the logic for initializing the runtime environment, including database connection, session management, and dispatcher setup.
/// </summary>
internal static class DxRuntimeFactory
{
    /// <summary>
    /// Asynchronously opens a DX workspace located at the specified root directory and constructs a <see cref="DxRuntime"/> instance.
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the DX workspace to open.</param>
    /// <param name="sessionId">An optional session ID to use. If not provided, the most recent active session will be used.</param>
    /// <param name="logger">An optional logger for recording runtime events and diagnostics.</param>
    /// <param name="ct">A cancellation token for aborting the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the constructed <see cref="DxRuntime"/> instance.</returns>
    /// <exception cref="DxException">Thrown if the specified directory is not a valid DX workspace or if the session cannot be found.</exception>
    internal static async Task<DxRuntime> OpenAsync(
        string workspaceRoot,
        string? sessionId = null,
        IDxLogger? logger = null,
        CancellationToken ct = default)
    {
        if (!DxRuntime.IsWorkspace(workspaceRoot))
            throw new DxException(
                DxError.WorkspaceNotInitialized,
                $"No DX workspace found at {workspaceRoot}. Run 'dxs init' first.");

        var connection = await DxDatabase.OpenAsync(workspaceRoot, ct: ct);
        await DxDatabase.MigrateAsync(connection, ct);

        sessionId ??= await connection.ExecuteScalarAsync<string>(
            "SELECT session_id FROM sessions WHERE closed_utc IS NULL ORDER BY created_utc DESC LIMIT 1")
            ?? throw new DxException(DxError.SessionNotFound, "No active session found.");

        var ignoreJson = await connection.ExecuteScalarAsync<string>(
            "SELECT ignore_set_json FROM sessions WHERE session_id = @sid",
            new { sid = sessionId })
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

        return new DxRuntime(
            connection,
            workspaceRoot,
            sessionId,
            ignoreSet,
            dispatcher,
            store,
            logger);
    }

    /// <summary>
    /// Determines whether the specified directory is the root of a DX workspace by checking
    /// for the presence of the <c>.dx/</c> directory and the workspace database file
    /// <c>snap.db</c>.
    /// </summary>
    /// <param name="workspaceRoot">The directory to check.</param>
    /// <returns><see langword="true"/> if the directory is a DX workspace root; otherwise, 
    /// <see langword="false"/>.</returns>
    public static bool IsWorkspace(string workspaceRoot)
    {
        var dxDir = Path.Combine(workspaceRoot, ".dx");
        var dbPath = Path.Combine(dxDir, "snap.db");

        return Directory.Exists(dxDir) && File.Exists(dbPath);
    }
}
