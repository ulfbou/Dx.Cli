using System.Text.Json;

namespace Dx.Core;

/// <summary>
/// Represents a fully materialized, deterministic set of exclusion rules.
/// </summary>
/// <remarks>
/// This type is the canonical, runtime-ready representation of all file exclusion semantics.
/// It contains only concrete, normalized patterns and performs no interpretation or policy evaluation.
/// 
/// Instances of <see cref="IgnoreSet"/> are created exclusively at genesis time via
/// <see cref="IgnoreSetFactory.Create"/> and persisted as JSON.
/// 
/// After deserialization, the instance must be immediately usable for deterministic evaluation
/// through <see cref="IsExcluded(string)"/> with no external dependencies.
/// </remarks>
public sealed class IgnoreSet
{
    /// <summary>
    /// Gets the normalized exclusion patterns.
    /// </summary>
    /// <remarks>
    /// Each entry represents a prefix match against a normalized relative path.
    /// Patterns must be pre-normalized (forward slashes, trimmed, no leading slash).
    /// </remarks>
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Determines whether the specified relative path is excluded.
    /// </summary>
    /// <param name="relativePath">The relative path to evaluate.</param>
    /// <returns>
    /// <c>true</c> if the path matches any exclusion pattern; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method performs a simple prefix match against all stored patterns.
    /// No additional rules, inference, or policy logic is applied.
    /// </remarks>
    public bool IsExcluded(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;

        foreach (var pattern in Patterns)
        {
            if (relativePath.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Serialises the exclusion prefix set to a JSON array string for persistence in
    /// the workspace database.
    /// </summary>
    /// <returns>A JSON array string of sorted exclusion prefix strings.</returns>
    public string Serialize()
        => JsonSerializer.Serialize(
            Patterns.OrderBy(p => p, StringComparer.Ordinal).ToArray());

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

        return new IgnoreSet
        {   
            Patterns = new HashSet<string>(arr, StringComparer.Ordinal).ToArray()
        };
    }
}
