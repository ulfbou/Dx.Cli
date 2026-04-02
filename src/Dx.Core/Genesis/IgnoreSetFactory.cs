namespace Dx.Core;

/// <summary>
/// Produces fully materialized <see cref="IgnoreSet"/> instances at genesis time.
/// </summary>
/// <remarks>
/// This is the single authoritative location for all exclusion policy decisions.
/// 
/// All default rules, user-provided exclusions, artifact handling, and build-output policies
/// are resolved here and converted into a concrete set of patterns.
/// </remarks>
public static class IgnoreSetFactory
{
    /// <summary>
    /// Creates a fully materialized <see cref="IgnoreSet"/>.
    /// </summary>
    /// <param name="artifactsDir">Optional artifacts directory to exclude.</param>
    /// <param name="userExcludes">User-provided exclusion paths.</param>
    /// <param name="includeBuildOutput">
    /// If <c>true</c>, build output directories are included; otherwise excluded.
    /// </param>
    /// <returns>A deterministic, fully resolved <see cref="IgnoreSet"/>.</returns>
    public static IgnoreSet Create(
        string? artifactsDir,
        IEnumerable<string> userExcludes,
        bool includeBuildOutput)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dx/",
            ".git/",
            ".hg/",
            ".svn/",
            "node_modules/",
            ".vs/",
            ".idea/"
        };

        if (!includeBuildOutput)
        {
            // Trailing slashes trigger recursive segment matching in IgnoreSet.IsExcluded
            patterns.Add("bin/");
            patterns.Add("obj/");
        }

        if (!string.IsNullOrWhiteSpace(artifactsDir))
        {
            patterns.Add(Normalize(artifactsDir));
        }

        foreach (var ex in userExcludes ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(ex))
            {
                patterns.Add(Normalize(ex));
            }
        }

        return new IgnoreSet
        {
            Patterns = patterns.ToArray()
        };
    }

    /// <summary>
    /// Normalizes a path into canonical ignore pattern form.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>A normalized pattern suitable for matching.</returns>
    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');

        // Ensure directories provided by users are treated as recursive directory ignores
        if (path.EndsWith('/') || path.EndsWith('\\'))
        {
            if (!normalized.EndsWith('/'))
            {
                normalized += "/";
            }
        }

        return normalized;
    }
}
