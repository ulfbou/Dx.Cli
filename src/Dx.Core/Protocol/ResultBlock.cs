namespace Dx.Core.Protocol;

/// <summary>
/// Represents a <c>%%RESULT</c> block containing the tool's response to a preceding
/// <c>%%REQUEST</c>, optionally embedding a nested <c>%%SNAP</c> block.
/// </summary>
/// <param name="For">The request type or identifier this result responds to.</param>
/// <param name="Status">The outcome status: <c>ok</c>, <c>error</c>, or <c>partial</c>.</param>
/// <param name="Args">Additional key-value metadata for the result.</param>
/// <param name="Body">The indentation-stripped result body text.</param>
/// <param name="Snap">An optional nested <see cref="SnapBlock"/>, if present.</param>
public sealed record ResultBlock(
    string  For,
    string  Status,
    IReadOnlyDictionary<string, string> Args,
    string  Body,
    SnapBlock? Snap
) : DxBlock
{
    /// <summary>
    /// Deconstructs the result block into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (for, status, args, body, snap) = resultBlock;</c>).
    /// </summary>
    /// <param name="for">When this method returns, contains the request type or identifier this result responds to.</param>
    /// <param name="status">When this method returns, contains the outcome status.</param>
    /// <param name="args">When this method returns, contains the dictionary of additional metadata for the result.</param>
    /// <param name="body">When this method returns, contains the body text of the result.</param>
    /// <param name="snap">When this method returns, contains the nested SnapBlock if present; otherwise null.</param>
    public void Deconstruct(
        out string @for,
        out string status,
        out IReadOnlyDictionary<string, string> args,
        out string body,
        out SnapBlock? snap)
    {
        @for = For;
        status = Status;
        args = Args;
        body = Body;
        snap = Snap;
    }
}
