namespace Dx.Core.Protocol;

/// <summary>
/// Represents a <c>%%FS</c> block that performs a filesystem operation such as moving,
/// deleting, re-encoding, or restoring a file.
/// </summary>
/// <param name="Op">
/// The operation name: <c>move</c>, <c>delete</c>, <c>encode</c>, <c>checkout</c>,
/// or <c>restore</c>.
/// </param>
/// <param name="Args">
/// A dictionary of key-value arguments for the operation (e.g. <c>from</c>, <c>to</c>,
/// <c>path</c>, <c>snap</c>).
/// </param>
public sealed record FsBlock(
    string Op,
    IReadOnlyDictionary<string, string> Args
) : DxBlock
{
    /// <summary>
    /// Deconstructs the filesystem block into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (op, args) = fsBlock;</c>).
    /// </summary>
    /// <param name="op">When this method returns, contains the filesystem operation name.</param>
    /// <param name="args">When this method returns, contains the dictionary of arguments for the operation.</param>
    public void Deconstruct(out string op, out IReadOnlyDictionary<string, string> args)
    {
        op = Op;
        args = Args;
    }
}
