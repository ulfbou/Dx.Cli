namespace Dx.Core;

public static class DxPath
{
    /// <summary>Normalize to forward-slash relative path.</summary>
    public static string Normalize(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace('\\', '/');
    }

    /// <summary>Convert normalized relative path back to OS absolute path.</summary>
    public static string ToAbsolute(string root, string normalized)
        => Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True if the normalized path stays within root (no .. escape).</summary>
    public static bool IsUnderRoot(string normalized)
        => !normalized.StartsWith("..") && !Path.IsPathRooted(normalized);

    /// <summary>Ensure path ends with '/' for prefix-based ignore matching.</summary>
    public static string AsDirectoryPrefix(string path)
        => path.TrimEnd('/') + "/";
}
