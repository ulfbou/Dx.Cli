namespace Dx.Core.Protocol;

/// <summary>
/// Represents a fully parsed DX document, consisting of a header and an ordered sequence
/// of typed blocks.
/// </summary>
/// <param name="Header">The parsed document header.</param>
/// <param name="Blocks">The ordered list of parsed blocks.</param>
public sealed record DxDocument(
    DxHeader Header,
    IReadOnlyList<DxBlock> Blocks)
{
    /// <summary>Gets an empty DX document.</summary>
    public static DxDocument Empty => new(
        new DxHeader("1.3", null, "tool", null, null, null, null, true, null),
        Array.Empty<DxBlock>());

    /// <summary>
    /// Gets a value indicating whether the document contains any mutation blocks —
    /// that is, any non-readonly <see cref="FileBlock"/>, any <see cref="PatchBlock"/>,
    /// or any <see cref="FsBlock"/> with a mutating operation.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, the dispatcher skips the full transaction lifecycle
    /// and only evaluates <see cref="RequestBlock"/> entries.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the document contains at least one mutation block;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool IsMutating => Blocks.Any(b =>
        b is FileBlock f && !f.ReadOnly ||
        b is PatchBlock ||
        b is FsBlock fs && fs.Op is "move" or "delete" or "encode" or "restore" or "checkout");

    /// <summary>
    /// Deconstructs the document into its component properties, allowing for
    /// deconstruction assignment syntax (e.g. <c>var (header, blocks) = document;</c>).
    /// </summary>
    /// <param name="header">When this method returns, contains the value of the Header property.</param>
    /// <param name="blocks">When this method returns, contains the value of the Blocks property.</param>
    public void Deconstruct(out DxHeader header, out IReadOnlyList<DxBlock> blocks)
    {
        header = Header;
        blocks = Blocks;
    }
}
