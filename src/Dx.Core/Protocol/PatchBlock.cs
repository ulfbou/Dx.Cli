namespace Dx.Core.Protocol;

/// <summary>
/// Represents a <c>%%PATCH</c> block that applies one or more surgical hunks to an
/// existing workspace file.
/// </summary>
/// <param name="Path">The workspace-relative path of the file to patch.</param>
/// <param name="Hunks">The ordered list of hunks to apply sequentially.</param>
public sealed record PatchBlock(
    string Path,
    IReadOnlyList<PatchHunk> Hunks
) : DxBlock
{
    /// <summary>
    /// Deconstructs the patch block into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (path, hunks) = patchBlock;</c>).
    /// </summary>
    /// <param name="path">When this method returns, contains the file path to patch.</param>
    /// <param name="hunks">When this method returns, contains the list of hunks to apply.</param>
    public void Deconstruct(out string path, out IReadOnlyList<PatchHunk> hunks)
    {
        path = Path;
        hunks = Hunks;
    }
}
