namespace Dx.Core
{
    /// <summary>
    /// Produces fully materialized <see cref="IgnoreSet"/> instances at genesis time.
    /// </summary>
    /// <remarks>
    /// This is the single authoritative location for all exclusion policy decisions.
    /// 
    /// All default rules, user-provided exclusions, artifact handling, and build-output policies
    /// are resolved here and converted into a concrete set of patterns.
    /// 
    /// The resulting <see cref="IgnoreSet"/> must be:
    /// - Fully self-sufficient
    /// - Fully deterministic
    /// - Free of flags or deferred logic
    /// 
    /// No other component in the system is permitted to re-evaluate or augment these rules.
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
                patterns.Add("bin/");
                patterns.Add("obj/");
            }

            if (!string.IsNullOrWhiteSpace(artifactsDir))
                patterns.Add(Normalize(artifactsDir));

            foreach (var ex in userExcludes ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(ex))
                    patterns.Add(Normalize(ex));
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
        /// <returns>A normalized pattern suitable for prefix matching.</returns>
        private static string Normalize(string path)
        {
            return path
                .Replace('\\', '/')
                .Trim()
                .TrimStart('/');
        }
    }
}
