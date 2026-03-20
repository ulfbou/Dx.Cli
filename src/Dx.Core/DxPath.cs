namespace Dx.Core;

/// <summary>
/// Provides path normalisation and validation utilities that enforce the DX convention
/// of forward-slash-separated relative paths rooted at the workspace root.
/// </summary>
/// <remarks>
/// DX documents and database records always store paths in their normalised form
/// (relative, forward-slash-separated, no leading slash). This class converts between
/// normalised paths and the OS-specific absolute paths required by filesystem APIs.
/// </remarks>
public static class DxPath
{
    /// <summary>
    /// Converts an absolute filesystem path to a normalised DX path relative to the
    /// workspace root, using forward slashes as the directory separator on all platforms.
    /// </summary>
    /// <param name="root">The absolute workspace root path.</param>
    /// <param name="fullPath">The absolute path to normalise.</param>
    /// <returns>
    /// A relative, forward-slash-separated path string, for example <c>src/main.cs</c>.
    /// </returns>
    public static string Normalize(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace('\\', '/');
    }

    /// <summary>
    /// Converts a normalised DX path back to an absolute OS-specific filesystem path.
    /// </summary>
    /// <param name="root">The absolute workspace root path to resolve against.</param>
    /// <param name="normalized">A normalised, forward-slash-separated relative path.</param>
    /// <returns>The absolute, OS-specific filesystem path.</returns>
    public static string ToAbsolute(string root, string normalized)
        => Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Determines whether a normalised path is safely contained within the workspace root.
    /// </summary>
    /// <param name="normalized">A normalised, forward-slash-separated relative path.</param>
    /// <returns>
    /// <see langword="true"/> when the path does not begin with <c>..</c> and is not
    /// an absolute path (both of which would indicate a path-traversal attempt);
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsUnderRoot(string normalized)
        => !normalized.StartsWith("..") && !Path.IsPathRooted(normalized);

    /// <summary>
    /// Ensures a normalised path ends with a trailing forward slash, making it suitable
    /// for use as a directory prefix in exclusion-list comparisons.
    /// </summary>
    /// <param name="path">The normalised path to convert to a directory prefix.</param>
    /// <returns>The path with exactly one trailing forward slash.</returns>
    public static string AsDirectoryPrefix(string path)
        => path.TrimEnd('/') + "/";
}
