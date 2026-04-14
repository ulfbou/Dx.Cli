namespace Dx.Core.Protocol;

using Dx.Core.Execution.Results;

using System.Text;

/// <summary>
/// Provides deterministic, byte-stable serialization of <see cref="DxResult"/> instances 
/// specifically for authoritative session logging.
/// </summary>
/// <remarks>
/// This serializer is strictly reserved for the <c>session_log</c> audit trail. It ensures that 
/// the outcome of every execution is recorded in a canonical DX format that is decoupled 
/// from CLI presentation logic and human-readable formatting.
/// </remarks>
public static class DxResultLoggingSerializer
{
    /// <summary>
    /// Serializes a <see cref="DxResult"/> into a canonical DX document string for logging.
    /// </summary>
    /// <param name="result">The execution result to serialize.</param>
    /// <returns>
    /// A <see langword="string"/> containing the DX-formatted result document.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    public static string Serialize(DxResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();

        sb.AppendLine("%%DX v1.3 type=result");

        sb.Append("%%RESULT ");
        sb.Append($"success={(result.IsSuccess ? "true" : "false")} ");
        sb.Append($"committed={(result.IsCommitted ? "true" : "false")} ");

        if (result.SnapId is not null)
        {
            sb.Append($"snap={result.SnapId} ");
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            sb.Append($"error=\"{Escape(result.Message)}\"");
        }

        sb.AppendLine();

        foreach (var block in result.Blocks)
        {
            sb.AppendLine($"%%BLOCK path=\"{block.Path ?? ""}\" status={(block.Success ? "applied" : "failed")}");
            if (!string.IsNullOrWhiteSpace(block.Detail))
            {
                sb.AppendLine($"    {block.Detail}");
            }
            sb.AppendLine("%%ENDBLOCK");
        }

        sb.AppendLine("%%END");
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
}
