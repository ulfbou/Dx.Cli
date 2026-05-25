using Dx.Core.Protocol;

namespace Dx.Core;

/// <summary>
/// Public lightweight host APIs that expose query-only workspace operations.
/// Internally constructs an <see cref="DxRuntime"/> via the internal factory and
/// returns query results without exposing <see cref="DxRuntime"/> type.
/// </summary>
public static class DxWorkspace
{
    /// <summary>
    /// Lists all snaps in the workspace, ordered by creation time descending (newest first).
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the workspace.</param>
    /// <param name="sessionId">Optional session ID for correlating logs.</param>
    /// <param name="logger">Optional logger for logging operations.</param>
    /// <returns>A list of <see cref="SnapInfo"/> objects representing the snaps in the workspace.</returns>
    /// <remarks>
    /// This method opens a new runtime instance to query the snaps. It is designed for read-only operations and does not modify the workspace state.
    /// The returned list is ordered by creation time in descending order, meaning the most recently created snaps will appear first in the list.
    /// </remarks>
    public static async Task<IReadOnlyList<SnapInfo>> ListSnapsAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.ListSnaps();
    }

    /// <summary>
    /// Retrieves the current head snap handle of the workspace.
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the workspace.</param>
    /// <param name="sessionId">Optional session ID for correlating logs.</param>
    /// <param name="logger">Optional logger for logging operations.</param>
    /// <returns>The snap handle of the current head, or null if there are no snaps
    /// in the workspace.</returns>
    /// <remarks>
    /// This method opens a new runtime instance to query the head snap. It is designed for
    /// read-only operations and does not modify the workspace state. If the workspace has no snaps, it will return <see langword="null"/>. Otherwise, 
    /// it returns the snap handle of the most recent snap, which is considered the head of the workspace.
    /// </remarks>
    public static async Task<string?> GetHeadAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.GetHead();
    }

    /// <summary>
    /// Retrieves the list of files included in a specific snap.
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the workspace.</param>
    /// <param name="handle">The snap handle for which to retrieve the file list.</
    /// param name="sessionId">Optional session ID for correlating logs.</param>
    /// <param name="logger">Optional logger for logging operations.</param>
    /// <returns>A list of <see cref="SnapFileInfo"/> objects representing the files
    /// included in the specified snap.</returns>
    /// <remarks>
    /// This method opens a new runtime instance to query the files of the specified snap. It
    /// is designed for read-only operations and does not modify the workspace state. The returned 
    /// list contains information about each file included in the snap, such as its path and size 
    /// in bytes.
    /// If the specified snap handle does not exist in the workspace, the behavior of this method 
    /// will depend on the implementation of the underlying runtime. It may throw an exception or 
    /// return an empty list. It is recommended to ensure that the snap handle exists before 
    /// calling this method to avoid potential errors.
    /// </remarks>
    public static async Task<IReadOnlyList<SnapFileInfo>> GetSnapFilesAsync(string workspaceRoot, string handle, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.GetSnapFiles(handle);
    }

    /// <summary>
    /// Computes the diff between two snaps, optionally filtered to a specific path.
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the workspace.</param>
    /// <param name="handleA">The snap handle of the first snap to compare.</
    /// param name="handleB">The snap handle of the second snap to compare.</param>
    /// <param name="filterPath">An optional path to filter the diff results. If
    /// provided, only changes to files under this path will be included in the results.</param>
    /// <param name="sessionId">Optional session ID for correlating logs.</param>
    /// <param name="logger">Optional logger for logging operations.</param>
    /// <returns>A list of <see cref="DiffEntry"/> objects representing the differences
    /// between the two specified snaps, optionally filtered by the provided path.</returns>
    /// <remarks>
    /// This method opens a new runtime instance to compute the diff between the specified snaps. It
    /// is designed for read-only operations and does not modify the workspace state. The returned 
    /// list contains entries for each file that differs between the two snaps, including the file 
    /// path and the type of change (added, modified, deleted).
    /// If the specified snap handles do not exist in the workspace, the behavior of this method
    /// will depend on the implementation of the underlying runtime. It may throw an exception or 
    /// return an empty list. It is recommended to ensure that both snap handles exist before calling 
    /// this method to avoid potential errors.
    /// </remarks>
    public static async Task<IReadOnlyList<DiffEntry>> DiffAsync(string workspaceRoot, string handleA, string handleB, string? filterPath = null, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.Diff(handleA, handleB, filterPath);
    }

    /// <summary>
    /// Retrieves the log entries for the workspace.
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the workspace.</param>
    /// <param name="limit">The maximum number of log entries to retrieve. Default is 100.</param>
    /// <param name="sessionId">Optional session ID for correlating logs.</param>
    /// <param name="logger">Optional logger for logging operations.</param>
    /// <returns>A list of <see cref="LogEntry"/> objects representing the log entries.</returns>
    /// <remarks>
    /// This method opens a new runtime instance to retrieve the log entries. It is designed for
    /// read-only operations and does not modify the workspace state. The returned list contains
    /// up to the specified number of log entries, ordered from most recent to oldest.
    /// If the workspace does not exist, the behavior of this method will depend on the implementation
    /// of the underlying runtime. It may throw an exception or return an empty list. It is recommended
    /// to ensure that the workspace exists before calling this method to avoid potential errors.
    /// </remarks>
    public static async Task<IReadOnlyList<LogEntry>> GetLogAsync(string workspaceRoot, int limit = 100, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.GetLog(limit);
    }

    /// <summary>
    /// Retrieves the ignore set for the workspace.
    /// </summary>
    /// <param name="workspaceRoot">The root directory of the workspace.</param>
    /// <param name="sessionId">Optional session ID for correlating logs.</param>
    /// <param name="logger">Optional logger for logging operations.</param>
    /// <returns>The <see cref="IgnoreSet"/> for the workspace.</returns>
    /// <remarks>
    /// This method opens a new runtime instance to retrieve the ignore set. It is designed for
    /// read-only operations and does not modify the workspace state. The returned ignore set contains
    /// the patterns and paths that are configured to be ignored in the workspace, which can be
    /// used to determine whether specific files or directories are excluded from snaps and diffs.
    /// If the workspace does not exist, the behavior of this method will depend on the implementation
    /// of the underlying runtime. It may throw an exception or return an empty ignore set.
    /// It is recommended to ensure that the workspace exists before calling this method to avoid potential errors.
    /// </remarks>
    public static async Task<IgnoreSet> GetIgnoreSetAsync(string workspaceRoot, string? sessionId = null, IDxLogger? logger = null)
    {
        var runtime = await DxRuntimeFactory.OpenAsync(workspaceRoot, sessionId, logger);
        return runtime.IgnoreSet;
    }

    /// <summary>
    /// Determines whether the specified directory is a valid DX workspace.
    /// </summary>
    /// <param name="workspaceRoot">The root directory to check.</param>
    /// <returns><see langword="true"/> if the specified directory is a valid DX
    /// workspace; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method checks for the presence of the necessary workspace structure and files to determine if the
    /// specified directory is a valid DX workspace. It does not require opening a runtime instance and can be 
    /// used to quickly verify if a directory is set up as a DX workspace before performing further operations.
    /// </remarks>
    public static bool IsWorkspace(string workspaceRoot)
        => DxRuntimeFactory.IsWorkspace(workspaceRoot);
}
