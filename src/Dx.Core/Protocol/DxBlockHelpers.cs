using System.Text;

namespace Dx.Core.Protocol;

/// <summary>
/// Shared path, encoding, and shell utilities used by block executors.
/// Extracted verbatim from <see cref="DxDispatcher"/> private methods.
/// </summary>
internal static class DxBlockHelpers
{
    internal static string ResolveAndValidate(string relOrAbs, string workspaceRoot)
    {
        var abs = Path.IsPathRooted(relOrAbs)
            ? relOrAbs
            : Path.GetFullPath(Path.Combine(workspaceRoot, relOrAbs));

        var norm = DxPath.Normalize(workspaceRoot, abs);
        if (!DxPath.IsUnderRoot(norm))
            throw new DxException(DxError.PathEscapesRoot, $"Path escapes root: {relOrAbs}");

        return abs;
    }

    internal static Encoding ResolveEncoding(string enc) => enc.ToLowerInvariant() switch
    {
        "utf-8" or "utf-8-no-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "utf-16-le" => Encoding.Unicode,
        "utf-16-be" => Encoding.BigEndianUnicode,
        "ascii" => Encoding.ASCII,
        "latin-1" => Encoding.Latin1,
        _ => new UTF8Encoding(false),
    };

    internal static (Encoding Enc, bool AddBom) ResolveEncodingWithBom(string enc) => enc.ToLowerInvariant() switch
    {
        "utf-8" => (new UTF8Encoding(false), false),
        "utf-8-no-bom" => (new UTF8Encoding(false), false),
        "utf-8-bom" => (new UTF8Encoding(false), true),
        "utf-16-le" => (Encoding.Unicode, true),
        "utf-16-be" => (Encoding.BigEndianUnicode, true),
        "ascii" => (Encoding.ASCII, false),
        "latin-1" => (Encoding.Latin1, false),
        _ => (new UTF8Encoding(false), false),
    };

    internal static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return new UTF8Encoding(false);
    }

    internal static byte[] StripBom(byte[] bytes, Encoding enc)
    {
        var preamble = enc.GetPreamble();
        if (preamble.Length == 0) return bytes;
        if (bytes.AsSpan().StartsWith(preamble))
            return bytes[preamble.Length..];

        return bytes;
    }

    internal static string NormalizeLineEndings(string text, string style) => style switch
    {
        "lf" => text.ReplaceLineEndings("\n"),
        "crlf" => text.ReplaceLineEndings("\r\n"),
        "cr" => text.ReplaceLineEndings("\r"),
        _ => text,
    };

    internal static (string Shell, string Args) GetShell(string command)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", $"/c {command}");
        return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
    }
}
