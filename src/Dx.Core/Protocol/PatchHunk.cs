namespace Dx.Core.Protocol;

/// <summary>
/// Represents a single hunk within a <c>%%PATCH</c> block.
/// </summary>
/// <param name="Operation">
/// The hunk operation type: <c>replace</c>, <c>insert</c>, or <c>delete</c>.
/// </param>
/// <param name="Target">
/// The hunk target specification, which may be a line range (<c>lines=N-M</c>),
/// a pattern (<c>pattern="..."</c>), or a positional anchor (<c>after-line=N</c>, etc.).
/// </param>
/// <param name="All">
/// When <see langword="true"/>, a pattern-based replace or delete applies to all matching
/// lines rather than just the first.
/// </param>
/// <param name="Body">
/// The replacement or insertion content (indentation-stripped), used for <c>replace</c>
/// and <c>insert</c> operations. Empty for <c>delete</c>.
/// </param>
public sealed record PatchHunk(
    string Operation,
    string Target,
    bool   All,
    string Body)
{
    /// <summary>
    /// Deconstructs the patch hunk into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (operation, target, all, body) = patchHunk;</c>).
    /// </summary>
    /// <param name="operation">When this method returns, contains the hunk operation type.</param>
    /// <param name="target">When this method returns, contains the hunk target specification.</param>
    /// <param name="all">When this method returns, contains the value indicating whether to apply to all matches.</param>
    /// <param name="body">When this method returns, contains the hunk body content.</param>
    public void Deconstruct(
        out string operation,
        out string target,
        out bool all,
        out string body)
    {
        operation = Operation;
        target = Target;
        all = All;
        body = Body;
    }
}
