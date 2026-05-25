namespace Dx.Core.Protocol;

/// <summary>
/// Represents a <c>%%SNAP</c> block, which records snapshot lineage metadata either
/// standalone or nested inside a <c>%%RESULT</c>.
/// </summary>
/// <param name="Id">The handle of the snapshot being described (e.g. <c>T0003</c>).</param>
/// <param name="Parent">The handle of the parent snapshot.</param>
/// <param name="CheckoutOf">
/// The handle of the snapshot this was checked out from, when applicable.
/// </param>
public sealed record SnapBlock(
    string  Id,
    string  Parent,
    string? CheckoutOf
) : DxBlock
{
    /// <summary>
    /// Deconstructs the snap block into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (id, parent, checkoutOf) = snapBlock;</c>).
    /// </summary>
    /// <param name="id">When this method returns, contains the handle of the snapshot being described.</param>
    /// <param name="parent">When this method returns, contains the handle of the parent snapshot.</param>
    /// <param name="checkoutOf">When this method returns, contains the handle of the snapshot this was checked out from, or <see langword="null"/> if not applicable.</param>
    public void Deconstruct(
        out string id,
        out string parent,
        out string? checkoutOf)
    {
        id = Id;
        parent = Parent;
        checkoutOf = CheckoutOf;
    }
}
