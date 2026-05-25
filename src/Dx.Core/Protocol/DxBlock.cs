namespace Dx.Core.Protocol;

/// <summary>
/// Abstract base type for all block nodes in a parsed DX document's IR (Intermediate
/// Representation). Each concrete block type corresponds to a <c>%%TOKEN</c> delimiter.
/// </summary>
public abstract record DxBlock
{
    public bool IsMutating => this switch
    {
        FileBlock f => !f.ReadOnly,
        PatchBlock => true,
        FsBlock fs => fs.Op is "move" or "delete" or "encode" or "restore" or "checkout",
        _ => false
    };
}
