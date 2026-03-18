using System.Text.Json;

namespace Dx.Core;

/// <summary>
/// Immutable ignore set for a session. Computed once at dx init, stored in DB.
/// Matching is prefix-based, case-sensitive, on normalized forward-slash paths.
/// </summary>
public sealed class IgnoreSet
{
    private static readonly string[] BuiltIn =
    [
        ".dx/", ".git/", ".hg/", ".svn/",
        "node_modules/", ".vs/", ".idea/",
    ];

    private static readonly string[] BuildOutput = ["bin/", "obj/"];

    private readonly HashSet<string> _prefixes;

    private IgnoreSet(HashSet<string> prefixes) => _prefixes = prefixes;

    public static IgnoreSet Build(
        string root,
        string? artifactsDir = null,
        IEnumerable<string>? userExcludes = null,
        bool includeBuildOutput = false)
    {
        var prefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in BuiltIn) prefixes.Add(p);
        if (!includeBuildOutput)
            foreach (var p in BuildOutput) prefixes.Add(p);

        if (artifactsDir is not null)
            prefixes.Add(NormalizeDeclared(root, artifactsDir));

        if (userExcludes is not null)
            foreach (var e in userExcludes)
                prefixes.Add(NormalizeDeclared(root, e));

        return new IgnoreSet(prefixes);
    }

    public bool IsExcluded(string root, string absolutePath)
    {
        var rel = DxPath.Normalize(root, absolutePath);
        foreach (var prefix in _prefixes)
            if (rel.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    public string Serialize()
        => JsonSerializer.Serialize(
            _prefixes.OrderBy(p => p, StringComparer.Ordinal).ToArray());

    public static IgnoreSet Deserialize(string json)
    {
        var arr = JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidOperationException("Invalid ignore set JSON.");
        return new IgnoreSet(new HashSet<string>(arr, StringComparer.Ordinal));
    }

    private static string NormalizeDeclared(string root, string path)
    {
        var abs = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(root, path));

        var norm = DxPath.Normalize(root, abs);

        if (!DxPath.IsUnderRoot(norm))
            throw new DxException(DxError.PathEscapesRoot,
                $"Exclusion path escapes root: '{path}'");

        return DxPath.AsDirectoryPrefix(norm);
    }
}
