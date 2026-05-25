using Dx.Core.Protocol;

namespace Dx.Core;

/// <summary>
/// Public lightweight host APIs that expose query-only workspace operations.
/// Internally constructs an <see cref="DxRuntime"/> via the internal factory and
/// returns query results without exposing <see cref="DxRuntime"/> type.
/// </summary>
public static class DxWorkspace
{
    public static async Task<IReadOnlyList<SnapInfo>> ListSnapsAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.ListSnaps();
    }

    public static async Task<string?> GetHeadAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.GetHead();
    }

    public static async Task<IReadOnlyList<SnapFileInfo>> GetSnapFilesAsync(string workspaceRoot, string handle, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.GetSnapFiles(handle);
    }

    public static async Task<IReadOnlyList<DiffEntry>> DiffAsync(string workspaceRoot, string handleA, string handleB, string? filterPath = null, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.Diff(handleA, handleB, filterPath);
    }

    public static async Task<IReadOnlyList<LogEntry>> GetLogAsync(string workspaceRoot, int limit = 100, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.GetLog(limit);
    }

    public static async Task<IgnoreSet> GetIgnoreSetAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.IgnoreSet;
    }

    public static bool IsWorkspace(string workspaceRoot)
        => DxRuntimeFactory.IsWorkspace(workspaceRoot);
}
