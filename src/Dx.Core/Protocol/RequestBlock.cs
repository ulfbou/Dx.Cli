namespace Dx.Core.Protocol;

/// <summary>
/// Represents a <c>%%REQUEST</c> block, which asks the tool to provide information or
/// to execute a command as a transaction gate.
/// </summary>
/// <param name="Type">
/// The request type: <c>file</c>, <c>tree</c>, <c>search</c>, <c>metadata</c>,
/// <c>git-status</c>, <c>diff</c>, <c>snaps</c>, or <c>run</c>.
/// </param>
/// <param name="Args">Additional key-value arguments for the request.</param>
/// <param name="Body">
/// The command body for <c>type=run</c> requests; empty for informational request types.
/// </param>
public sealed record RequestBlock(
    string Type,
    IReadOnlyDictionary<string, string> Args,
    string Body
) : DxBlock
{
    /// <summary>
    /// Deconstructs the request block into its component properties, allowing for deconstruction assignment syntax
    ///  (e.g. <c>var (type, args, body) = requestBlock;</c>).
    /// </summary>
    /// <param name="type">When this method returns, contains the request type.</param>
    /// <param name="args">When this method returns, contains the dictionary of arguments for the request.</param>
    /// <param name="body">When this method returns, contains the command body for run requests or an empty string 
    /// for informational requests.</param>
    public void Deconstruct(
        out string type,
        out IReadOnlyDictionary<string, string> args,
        out string body)
    {
        type = Type;
        args = Args;
        body = Body;
    }
}
