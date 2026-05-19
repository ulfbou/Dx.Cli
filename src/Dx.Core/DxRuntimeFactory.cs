using Dapper;

using Dx.Core.Protocol;
using Dx.Core.Storage;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

internal static class DxRuntimeFactory
{
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

        // ✅ AUTHORITY CONSTRUCTION (ONLY PLACE)
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
