using Dx.Core;

namespace Dx.Core.Genesis;

/// <summary>
/// The unified, deterministic source of truth for ignore-set construction.
/// Used by: init, session new, and pack.
/// </summary>
public static class IgnoreSetFactory
{
    /// <summary>
    /// Creates an IgnoreSet based on the given parameters. This method encapsulates 
    /// the logic for determining which files and directories should be ignored 
    /// during various operations (e.g., session creation, packing). It takes into 
    /// account the root directory, artifacts directory, user-defined excludes, and 
    /// whether to include build output in the ignore set.
    /// </summary>
    /// <param name="root">The root directory of the workspace.</param>
    /// <param name="artifactsDir">The directory where artifacts are stored.</param>
    /// <param name="userExcludes">A collection of user-defined paths to exclude.</param>
    /// <param name="includeBuildOutput">Whether to include build output in the ignore set.</param>
    /// <returns>An IgnoreSet configured based on the provided parameters.</returns>
    public static IgnoreSet Create(
        string root,
        string? artifactsDir,
        IEnumerable<string>? userExcludes,
        bool includeBuildOutput)
    {
        return IgnoreSet.Build(
            root,
            artifactsDir,
            userExcludes,
            includeBuildOutput);
    }
}
