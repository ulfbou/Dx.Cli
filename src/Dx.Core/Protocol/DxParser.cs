using System.Text;
using System.Text.RegularExpressions;

namespace Dx.Core.Protocol;

/// <summary>
/// Parses DX document text into a typed intermediate representation (<see cref="DxDocument"/>).
/// </summary>
/// <remarks>
/// <para>
/// The parser is single-pass and line-oriented. Delimiter tokens (lines beginning with
/// <c>%%</c>) are only recognised at column zero — leading whitespace disqualifies a line
/// from being treated as a delimiter.
/// </para>
/// <para>
/// Body lines inside a block are indentation-stripped: exactly four leading spaces or one
/// leading tab is removed. This convention allows DX documents to be stored with a uniform
/// visual indent while the extracted content faithfully reproduces the original source.
/// </para>
/// </remarks>
public static partial class DxParser
{
    [GeneratedRegex(@"^%%DX\s+v(?<ver>\S+)(?<args>.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^%%(?<token>[A-Z]+)(?<args>.*)$")]
    private static partial Regex DelimiterRegex();

    [GeneratedRegex(@"@@\s*(?<op>replace|insert|delete)\s+(?<target>.+?)(?:\s+all=""true"")?$")]
    private static partial Regex HunkStartRegex();

    // ── Public entry points ────────────────────────────────────────────────────

    /// <summary>
    /// Parses a DX document from a string.
    /// </summary>
    /// <param name="text">The full text of the DX document.</param>
    /// <returns>
    /// A tuple containing the parsed <see cref="DxDocument"/> (or <see langword="null"/>
    /// when parse errors are fatal) and a read-only list of <see cref="ParseError"/> records.
    /// When errors are present the document is <see langword="null"/>.
    /// </returns>
    public static (DxDocument? Doc, IReadOnlyList<ParseError> Errors)
        ParseText(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return Parse(lines);
    }

    /// <summary>
    /// Parses a DX document from a file on disk, reading it as UTF-8.
    /// </summary>
    /// <param name="path">The absolute path of the file to parse.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// A task that resolves to a tuple of the parsed document and any parse errors.
    /// </returns>
    public static async Task<(DxDocument? Doc, IReadOnlyList<ParseError> Errors)>
        ParseFileAsync(string path, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        return ParseText(text);
    }

    // ── Core parser ────────────────────────────────────────────────────────────

    /// <summary>
    /// Core line-by-line parser that builds the document IR from a pre-split line array.
    /// </summary>
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

        // ── Parse %%DX header ──────────────────────────────────────────────────
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

        // ── Parse blocks until %%END ───────────────────────────────────────────
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

    // ── Header parsing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the version and key-value argument portion of a <c>%%DX</c> header line
    /// into a <see cref="DxHeader"/> record.
    /// </summary>
    private static DxHeader ParseHeader(string version, string argsStr)
    {
        var args = ParseArgs(argsStr);
        return new DxHeader(
            Version:      version,
            Session:      args.GetValueOrDefault("session"),
            Author:       args.GetValueOrDefault("author"),
            Title:        args.GetValueOrDefault("title"),
            Base:         args.GetValueOrDefault("base"),
            Root:         args.GetValueOrDefault("root"),
            Target:       args.GetValueOrDefault("target"),
            ReadOnly:     args.GetValueOrDefault("readonly", "false") == "true",
            ArtifactsDir: args.GetValueOrDefault("artifacts_dir"));
    }

    // ── Body reading ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads lines from <paramref name="lines"/> starting at <paramref name="start"/>
    /// until a <c>%%ENDBLOCK</c> delimiter at column zero is encountered, stripping one
    /// level of indentation from each body line.
    /// </summary>
    /// <param name="lines">The full line array of the document being parsed.</param>
    /// <param name="start">The zero-based index of the first body line.</param>
    /// <param name="n">The total number of lines.</param>
    /// <returns>
    /// A tuple of the stripped body string and the zero-based index of the next line to
    /// parse after consuming the <c>%%ENDBLOCK</c> delimiter.
    /// </returns>
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
    /// Reads a <c>%%RESULT</c> block body, handling an optional nested <c>%%SNAP</c>
    /// block before the closing <c>%%ENDBLOCK</c>.
    /// </summary>
    /// <returns>
    /// A tuple of the body string, the next line index after the outer <c>%%ENDBLOCK</c>,
    /// and the nested <see cref="SnapBlock"/> if one was present.
    /// </returns>
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

    // ── Hunk parsing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parses all hunks from the body text of a <c>%%PATCH</c> block.
    /// Each hunk begins with an <c>@@</c> operation header and ends with a closing <c>@@</c>.
    /// </summary>
    /// <param name="patchBody">The indentation-stripped body of the patch block.</param>
    /// <param name="errors">The error list to append parse errors to.</param>
    /// <param name="baseLineNo">The line number of the start of the patch block, for error reporting.</param>
    /// <returns>A read-only list of parsed <see cref="PatchHunk"/> records.</returns>
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

    // ── Argument parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses a space-separated list of <c>key=value</c> and <c>key="quoted value"</c>
    /// argument pairs from a block or header argument string.
    /// </summary>
    /// <param name="argsStr">The raw argument string to parse.</param>
    /// <returns>
    /// A case-insensitive dictionary mapping argument keys to their string values.
    /// </returns>
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

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strips exactly one level of indentation from a line: four leading spaces or one
    /// leading tab. Returns the line unchanged when neither is present.
    /// </summary>
    private static string StripOneIndent(string line)
    {
        if (line.StartsWith("    ")) return line[4..];  // 4 spaces
        if (line.StartsWith('\t'))   return line[1..];  // 1 tab
        return line;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a line should be treated as an inter-block
    /// comment and skipped during parsing (i.e. it does not begin with <c>%%</c> or
    /// <c>@@</c>).
    /// </summary>
    private static bool IsComment(string line)
        => !line.StartsWith("%%") && !line.StartsWith("@@");
}
