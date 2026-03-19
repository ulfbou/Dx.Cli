using System.Text.Json;

namespace Dx.Core;

/// <summary>
/// Represents the set of file and directory path prefixes that are excluded from DX
/// snapshot manifests for a given session.
/// </summary>
/// <remarks>
/// <para>
/// The ignore set is built once at workspace or session initialisation time and
/// serialised into the database as a JSON array of normalised prefix strings. It is
/// deserialised on every <see cref="DxRuntime.Open"/> call and used by
/// <see cref="ManifestBuilder"/> and <see cref="RollbackEngine"/> to filter the working
/// tree consistently across all operations.
/// </para>
/// <para>
/// Built-in exclusions (<c>.dx/</c>, <c>.git/</c>, VCS directories, IDE caches) are
/// always applied and cannot be overridden. Build output directories (<c>bin/</c>,
/// <c>obj/</c>) are excluded by default but can be included via
/// <paramref name="includeBuildOutput"/> in <see cref="Build"/>.
/// </para>
/// </remarks>
public sealed class IgnoreSet
{
    /// <summary>Directory prefixes that are unconditionally excluded from every snapshot.</summary>
    private static readonly string[] BuiltIn =
    [
        ".dx/", ".git/", ".hg/", ".svn/",
        "node_modules/", ".vs/", ".idea/",
    ];

    /// <summary>Build output directory prefixes excluded by default.</summary>
    private static readonly string[] BuildOutput = ["bin/", "obj/"];

    private readonly HashSet<string> _prefixes;

    private IgnoreSet(HashSet<string> prefixes) => _prefixes = prefixes;

    /// <summary>
    /// Constructs an <see cref="IgnoreSet"/> from the provided configuration, combining
    /// built-in exclusions with any user-supplied paths.
    /// </summary>
    /// <param name="root">
    /// The absolute workspace root path, used to normalise relative exclusion paths.
    /// </param>
    /// <param name="artifactsDir">
    /// An optional directory to exclude (e.g. a CI artifact output directory).
    /// May be relative to <paramref name="root"/> or absolute.
    /// </param>
    /// <param name="userExcludes">
    /// An optional sequence of additional paths to exclude. Each may be relative to
    /// <paramref name="root"/> or absolute.
    /// </param>
    /// <param name="includeBuildOutput">
    /// When <see langword="true"/>, <c>bin/</c> and <c>obj/</c> are not added to the
    /// exclusion set and will appear in snapshots.
    /// </param>
    /// <returns>A fully constructed <see cref="IgnoreSet"/>.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.PathEscapesRoot"/> when any supplied path normalises
    /// to a location outside the workspace root.
    /// </exception>
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

    /// <summary>
    /// Determines whether the given file should be excluded from the snapshot manifest.
    /// </summary>
    /// <param name="root">The absolute workspace root path.</param>
    /// <param name="absolutePath">The absolute path of the file to test.</param>
    /// <returns>
    /// <see langword="true"/> when the file's normalised relative path starts with any
    /// registered exclusion prefix; otherwise <see langword="false"/>.
    /// </returns>
    public bool IsExcluded(string root, string absolutePath)
    {
        var rel = DxPath.Normalize(root, absolutePath);
        foreach (var prefix in _prefixes)
            if (rel.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Serialises the exclusion prefix set to a JSON array string for persistence in
    /// the workspace database.
    /// </summary>
    /// <returns>A JSON array string of sorted exclusion prefix strings.</returns>
    public string Serialize()
        => JsonSerializer.Serialize(
            _prefixes.OrderBy(p => p, StringComparer.Ordinal).ToArray());

    /// <summary>
    /// Deserialises an <see cref="IgnoreSet"/> from a JSON array string previously
    /// produced by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="json">The JSON array string to deserialise.</param>
    /// <returns>The reconstructed <see cref="IgnoreSet"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="json"/> cannot be deserialised as a string array.
    /// </exception>
    public static IgnoreSet Deserialize(string json)
    {
        var arr = JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidOperationException("Invalid ignore set JSON.");
        return new IgnoreSet(new HashSet<string>(arr, StringComparer.Ordinal));
    }

    /// <summary>
    /// Normalises a user-declared exclusion path to a forward-slash-separated directory
    /// prefix relative to the workspace root, validating that it does not escape the root.
    /// </summary>
    /// <param name="root">The absolute workspace root path.</param>
    /// <param name="path">The path to normalise (relative or absolute).</param>
    /// <returns>A directory prefix string ending with <c>/</c>.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.PathEscapesRoot"/> when the normalised path does
    /// not remain within the workspace root.
    /// </exception>
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
