namespace Dx.Core.Protocol;

using Dx.Core.Execution.Results;

using System.Text;

/// <summary>
/// Provides deterministic, byte-stable serialization of <see cref="DxResult"/> instances 
/// specifically for authoritative session logging.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Determinism Contract:</strong>
/// The output of this serializer must be byte-stable for a given 
/// <see cref="DxResult"/>.
/// </para>
/// <para>
/// Do not introduce ordering based on runtime state, collections 
/// with unstable iteration order, timestamps, or environment variables.
/// </para>
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
        sb.Append($"status={result.Status.ToString().ToLowerInvariant()} ");
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

        if (result.Metadata?.Count > 0)
        {
            sb.AppendLine("%%METADATA");
            foreach (var kvp in result.Metadata.OrderBy(k => k.Key))
            {
                sb.AppendLine($"    {kvp.Key}: {FormatValue(kvp.Value)}");
            }
            sb.AppendLine("%%ENDMETADATA");
        }

        foreach (var op in result.Blocks)
        {
            SerializeOperation(op, sb);
        }

        sb.AppendLine("%%END");
        return sb.ToString();
    }

    private static void SerializeOperation(OperationResult op, StringBuilder sb)
    {
        string baseType = op.BlockType.Split(':')[0].ToUpperInvariant();
        string tag = baseType switch
        {
            "FILE" => "%%FILE",
            "PATCH" => "%%PATCH",
            "FS" => "%%FS",
            "REQUEST" => "%%REQUEST",
            _ => "%%BLOCK"
        };

        sb.Append($"{tag} ");
        if (!string.IsNullOrWhiteSpace(op.Path)) sb.Append($"path=\"{op.Path}\" ");
        sb.Append($"status={(op.Success ? "applied" : "failed")}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(op.Detail))
        {
            var lines = op.Detail.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                sb.AppendLine($"    {line}");
            }
        }

        sb.AppendLine("%%ENDBLOCK");
    }

    private static string FormatValue(object? val) => val switch
    {
        null => "null",
        string s => $"\"{Escape(s)}\"",
        bool b => b ? "true" : "false",
        _ => val.ToString() ?? "null"
    };

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\r", "")
             .Replace("\n", " ");
}
