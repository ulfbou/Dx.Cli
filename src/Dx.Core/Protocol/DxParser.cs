using System.Text;
using System.Text.RegularExpressions;

namespace Dx.Core.Protocol;

/// <summary>
/// Single-pass, line-oriented parser for DX documents.
/// Delimiter rule: %%TOKEN is only recognized at column 0 (no leading whitespace).
/// Body lines are indentation-stripped (exactly one level: 4 spaces or 1 tab).
/// </summary>
public static partial class DxParser
{
    [GeneratedRegex(@"^%%DX\s+v(?<ver>\S+)(?<args>.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^%%(?<token>[A-Z]+)(?<args>.*)$")]
    private static partial Regex DelimiterRegex();

    [GeneratedRegex(@"@@\s*(?<op>replace|insert|delete)\s+(?<target>.+?)(?:\s+all=""true"")?$")]
    private static partial Regex HunkStartRegex();

    // ── Public entry points ────────────────────────────────────────────────

    public static (DxDocument? Doc, IReadOnlyList<ParseError> Errors)
        ParseText(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return Parse(lines);
    }

    public static async Task<(DxDocument? Doc, IReadOnlyList<ParseError> Errors)>
        ParseFileAsync(string path, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        return ParseText(text);
    }

    // ── Core parser ────────────────────────────────────────────────────────

    private static (DxDocument? Doc, IReadOnlyList<ParseError> Errors)
        Parse(string[] lines)
    {
        var errors = new List<ParseError>();
        DxHeader? header = null;
        var blocks  = new List<DxBlock>();
        var i       = 0;
        var n       = lines.Length;

        // Skip leading blank lines
        while (i < n && string.IsNullOrWhiteSpace(lines[i])) i++;

        // ── Parse %%DX header ──────────────────────────────────────────────
        if (i >= n || !lines[i].StartsWith("%%DX ", StringComparison.Ordinal))
        {
            errors.Add(new(i + 1, "Document must begin with %%DX v<version>"));
            return (null, errors);
        }

        var hm = HeaderRegex().Match(lines[i]);
        if (!hm.Success)
        {
            errors.Add(new(i + 1, $"Malformed %%DX header: {lines[i]}"));
            return (null, errors);
        }

        header = ParseHeader(hm.Groups["ver"].Value, hm.Groups["args"].Value.Trim());
        i++;

        // ── Parse blocks until %%END ───────────────────────────────────────
        while (i < n)
        {
            var line = lines[i];

            if (line == "%%END") break;
            if (string.IsNullOrWhiteSpace(line) || IsComment(line)) { i++; continue; }

            // Only column-0 %% lines are delimiters
            if (!line.StartsWith("%%"))  { i++; continue; }

            var dm = DelimiterRegex().Match(line);
            if (!dm.Success) { i++; continue; }

            var token = dm.Groups["token"].Value;
            var args  = ParseArgs(dm.Groups["args"].Value.Trim());
            i++;

            switch (token)
            {
                case "FILE":
                {
                    var (body, endLine) = ReadBody(lines, i, n);
                    i = endLine;
                    blocks.Add(new FileBlock(
                        Path:       args.GetValueOrDefault("path", ""),
                        Encoding:   args.GetValueOrDefault("encoding", "utf-8"),
                        ReadOnly:   args.GetValueOrDefault("readonly", "false") == "true",
                        Lines:      args.GetValueOrDefault("lines"),
                        Create:     args.GetValueOrDefault("create", "true") == "true",
                        IfContains: args.GetValueOrDefault("if-contains"),
                        IfLine:     args.GetValueOrDefault("if-line"),
                        Content:    body));
                    break;
                }
                case "PATCH":
                {
                    var (body, endLine) = ReadBody(lines, i, n);
                    i = endLine;
                    var hunks = ParseHunks(body, errors, i);
                    blocks.Add(new PatchBlock(
                        Path:  args.GetValueOrDefault("path", ""),
                        Hunks: hunks));
                    break;
                }
                case "FS":
                    var (fsBody, fsEnd) = ReadBody(lines, i, n);
                    i = fsEnd;
                    blocks.Add(new FsBlock(
                        Op:   args.GetValueOrDefault("op", ""),
                        Args: args));
                    break;

                case "REQUEST":
                {
                    var (reqBody, reqEnd) = ReadBody(lines, i, n);
                    i = reqEnd;
                    blocks.Add(new RequestBlock(
                        Type: args.GetValueOrDefault("type", ""),
                        Args: args,
                        Body: reqBody.Trim()));
                    break;
                }
                case "RESULT":
                {
                    var (resBody, resEnd, snap) = ReadResultBody(lines, i, n);
                    i = resEnd;
                    blocks.Add(new ResultBlock(
                        For:    args.GetValueOrDefault("for", ""),
                        Status: args.GetValueOrDefault("status", "ok"),
                        Args:   args,
                        Body:   resBody,
                        Snap:   snap));
                    break;
                }
                case "NOTE":
                {
                    var (noteBody, noteEnd) = ReadBody(lines, i, n);
                    i = noteEnd;
                    blocks.Add(new NoteBlock(noteBody));
                    break;
                }
                case "SNAP":
                    // Standalone snap (not inside RESULT) — informational
                    blocks.Add(new SnapBlock(
                        Id:         args.GetValueOrDefault("id", ""),
                        Parent:     args.GetValueOrDefault("parent", ""),
                        CheckoutOf: args.GetValueOrDefault("checkout-of")));
                    i++;
                    break;

                default:
                    errors.Add(new(i, $"Unknown block type: %%{token}"));
                    // Skip to %%ENDBLOCK
                    while (i < n && lines[i] != "%%ENDBLOCK") i++;
                    if (i < n) i++;
                    break;
            }
        }

        if (errors.Count > 0) return (null, errors);

        return (new DxDocument(header, blocks), errors);
    }

    // ── Header parsing ─────────────────────────────────────────────────────

    private static DxHeader ParseHeader(string version, string argsStr)
    {
        var args = ParseArgs(argsStr);
        return new DxHeader(
            Version:      version,
            Session:      args.GetValueOrDefault("session"),
            Author:       args.GetValueOrDefault("author"),
            Base:         args.GetValueOrDefault("base"),
            Root:         args.GetValueOrDefault("root"),
            Target:       args.GetValueOrDefault("target"),
            ReadOnly:     args.GetValueOrDefault("readonly", "false") == "true",
            ArtifactsDir: args.GetValueOrDefault("artifacts_dir"));
    }

    // ── Body reading ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads lines until %%ENDBLOCK at column 0. Strips one indentation level.
    /// Returns (body, next_line_index).
    /// </summary>
    private static (string Body, int NextLine) ReadBody(string[] lines, int start, int n)
    {
        var sb = new StringBuilder();
        var i  = start;

        while (i < n && lines[i] != "%%ENDBLOCK")
        {
            sb.AppendLine(StripOneIndent(lines[i]));
            i++;
        }

        if (i < n) i++; // consume %%ENDBLOCK

        return (sb.ToString(), i);
    }

    /// <summary>
    /// Reads a RESULT body, handling nested %%SNAP block before %%ENDBLOCK.
    /// </summary>
    private static (string Body, int NextLine, SnapBlock? Snap)
        ReadResultBody(string[] lines, int start, int n)
    {
        var sb   = new StringBuilder();
        var i    = start;
        SnapBlock? snap = null;

        while (i < n && lines[i] != "%%ENDBLOCK")
        {
            var line = lines[i];

            if (line.StartsWith("%%SNAP ", StringComparison.Ordinal))
            {
                var args = ParseArgs(line["%%SNAP ".Length..].Trim());
                snap = new SnapBlock(
                    Id:         args.GetValueOrDefault("id", ""),
                    Parent:     args.GetValueOrDefault("parent", ""),
                    CheckoutOf: args.GetValueOrDefault("checkout-of"));
                i++;
                // %%SNAP is self-closing inside RESULT
                if (i < n && lines[i] == "%%ENDBLOCK") { i++; } // snap's own ENDBLOCK
                continue;
            }

            sb.AppendLine(StripOneIndent(line));
            i++;
        }

        if (i < n) i++; // consume outer %%ENDBLOCK

        return (sb.ToString(), i, snap);
    }

    // ── Hunk parsing ───────────────────────────────────────────────────────

    private static IReadOnlyList<PatchHunk> ParseHunks(
        string patchBody, List<ParseError> errors, int baseLineNo)
    {
        var hunks = new List<PatchHunk>();
        var lines = patchBody.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (!line.StartsWith("@@")) { i++; continue; }

            // Closing @@ (no operation keyword) = end of hunk body
            if (line == "@@") { i++; continue; }

            var m = HunkStartRegex().Match(line);
            if (!m.Success)
            {
                errors.Add(new(baseLineNo + i, $"Malformed hunk header: {line}"));
                i++;
                continue;
            }

            var op     = m.Groups["op"].Value;
            var target = m.Groups["target"].Value.Trim();
            var all    = line.Contains("all=\"true\"", StringComparison.Ordinal);
            i++;

            // Read body until closing @@
            var body = new StringBuilder();
            while (i < lines.Length && lines[i].TrimEnd() != "@@")
            {
                body.AppendLine(lines[i]);
                i++;
            }
            if (i < lines.Length) i++; // consume closing @@

            hunks.Add(new PatchHunk(op, target, all, body.ToString()));
        }

        return hunks;
    }

    // ── Argument parsing ───────────────────────────────────────────────────

    /// <summary>
    /// Parses key=value and key="quoted value" argument strings.
    /// </summary>
    private static Dictionary<string, string> ParseArgs(string argsStr)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(argsStr)) return result;

        var remaining = argsStr.Trim();

        while (remaining.Length > 0)
        {
            var eqIdx = remaining.IndexOf('=');
            if (eqIdx < 0) break;

            var key = remaining[..eqIdx].Trim();
            remaining = remaining[(eqIdx + 1)..];

            string value;
            if (remaining.StartsWith('"'))
            {
                var closeQuote = remaining.IndexOf('"', 1);
                if (closeQuote < 0) { value = remaining[1..]; remaining = ""; }
                else
                {
                    value     = remaining[1..closeQuote];
                    remaining = remaining[(closeQuote + 1)..].TrimStart();
                }
            }
            else
            {
                var spaceIdx = remaining.IndexOf(' ');
                if (spaceIdx < 0) { value = remaining; remaining = ""; }
                else
                {
                    value     = remaining[..spaceIdx];
                    remaining = remaining[(spaceIdx + 1)..].TrimStart();
                }
            }

            if (!string.IsNullOrWhiteSpace(key))
                result[key] = value;
        }

        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string StripOneIndent(string line)
    {
        if (line.StartsWith("    ")) return line[4..];  // 4 spaces
        if (line.StartsWith('\t'))   return line[1..];  // 1 tab
        return line;
    }

    private static bool IsComment(string line)
        => !line.StartsWith("%%") && !line.StartsWith("@@");
}
