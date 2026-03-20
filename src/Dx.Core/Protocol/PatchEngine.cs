namespace Dx.Core.Protocol;

/// <summary>
/// Applies an ordered sequence of <see cref="PatchHunk"/> operations to file content,
/// producing the patched result as a string.
/// </summary>
/// <remarks>
/// <para>
/// All hunks in the sequence must succeed. If any hunk fails (e.g. because a pattern is
/// not found or a line range is out of bounds) a <see cref="DxException"/> is thrown with
/// <see cref="DxError.ParseError"/>. The caller is responsible for rolling back the working
/// tree when this occurs.
/// </para>
/// <para>
/// Line endings in the input are normalised to <c>\n</c> for processing and restored to
/// <see cref="Environment.NewLine"/> in the output, with a trailing newline appended when
/// the original content ended with one.
/// </para>
/// </remarks>
public static class PatchEngine
{
    /// <summary>
    /// Applies all <paramref name="hunks"/> sequentially to <paramref name="content"/>,
    /// returning the fully patched file text.
    /// </summary>
    /// <param name="content">The original file content as a string.</param>
    /// <param name="hunks">The ordered list of hunks to apply.</param>
    /// <returns>The patched file content as a string.</returns>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> when any hunk fails its precondition
    /// (unknown operation, out-of-range line number, pattern not found).
    /// </exception>
    public static string Apply(string content, IReadOnlyList<PatchHunk> hunks)
    {
        var lines = SplitLines(content);

        foreach (var hunk in hunks)
            lines = ApplyHunk(lines, hunk);

        return string.Join(Environment.NewLine, lines)
               + (content.EndsWith('\n') ? Environment.NewLine : "");
    }

    /// <summary>
    /// Dispatches a single hunk to the appropriate handler based on its
    /// <see cref="PatchHunk.Operation"/>.
    /// </summary>
    private static List<string> ApplyHunk(List<string> lines, PatchHunk hunk)
    {
        return hunk.Operation switch
        {
            "replace" => ApplyReplace(lines, hunk),
            "insert"  => ApplyInsert(lines, hunk),
            "delete"  => ApplyDelete(lines, hunk),
            _ => throw new DxException(DxError.ParseError,
                     $"Unknown hunk operation: {hunk.Operation}")
        };
    }

    // ── replace ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles the <c>replace</c> hunk operation, supporting both line-range and
    /// pattern-based targets.
    /// </summary>
    private static List<string> ApplyReplace(List<string> lines, PatchHunk hunk)
    {
        var bodyLines = SplitLines(hunk.Body.TrimEnd());

        if (hunk.Target.StartsWith("lines="))
        {
            var (start, end) = ParseLineRange(hunk.Target["lines=".Length..]);
            ValidateRange(lines, start, end, hunk);

            var result = new List<string>(lines);
            result.RemoveRange(start - 1, end - start + 1);
            result.InsertRange(start - 1, bodyLines);
            return result;
        }

        if (hunk.Target.StartsWith("pattern="))
        {
            var pattern = UnquotePattern(hunk.Target["pattern=".Length..]);
            if (hunk.All)
            {
                return lines.SelectMany(l =>
                    l.Contains(pattern, StringComparison.Ordinal)
                        ? bodyLines
                        : (IEnumerable<string>)[l]).ToList();
            }
            else
            {
                var idx = FindFirst(lines, pattern, hunk);
                var result = new List<string>(lines);
                result.RemoveAt(idx);
                result.InsertRange(idx, bodyLines);
                return result;
            }
        }

        throw new DxException(DxError.ParseError,
            $"Unrecognized replace target: {hunk.Target}");
    }

    // ── insert ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles the <c>insert</c> hunk operation, supporting after-line, before-line,
    /// after-pattern, and before-pattern targets.
    /// </summary>
    private static List<string> ApplyInsert(List<string> lines, PatchHunk hunk)
    {
        var bodyLines = SplitLines(hunk.Body.TrimEnd());

        if (hunk.Target.StartsWith("after-line="))
        {
            var lineNo = ParseSingleLine(hunk.Target["after-line=".Length..]);
            ValidateLine(lines, lineNo, hunk);
            var result = new List<string>(lines);
            result.InsertRange(lineNo, bodyLines); // after lineNo (0-based: lineNo)
            return result;
        }

        if (hunk.Target.StartsWith("before-line="))
        {
            var lineNo = ParseSingleLine(hunk.Target["before-line=".Length..]);
            ValidateLine(lines, lineNo, hunk);
            var result = new List<string>(lines);
            result.InsertRange(lineNo - 1, bodyLines);
            return result;
        }

        if (hunk.Target.StartsWith("after-pattern="))
        {
            var pattern = UnquotePattern(hunk.Target["after-pattern=".Length..]);
            var idx     = FindFirst(lines, pattern, hunk);
            var result  = new List<string>(lines);
            result.InsertRange(idx + 1, bodyLines);
            return result;
        }

        if (hunk.Target.StartsWith("before-pattern="))
        {
            var pattern = UnquotePattern(hunk.Target["before-pattern=".Length..]);
            var idx     = FindFirst(lines, pattern, hunk);
            var result  = new List<string>(lines);
            result.InsertRange(idx, bodyLines);
            return result;
        }

        throw new DxException(DxError.ParseError,
            $"Unrecognized insert target: {hunk.Target}");
    }

    // ── delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles the <c>delete</c> hunk operation, supporting line-range and
    /// pattern-based targets.
    /// </summary>
    private static List<string> ApplyDelete(List<string> lines, PatchHunk hunk)
    {
        if (hunk.Target.StartsWith("lines="))
        {
            var (start, end) = ParseLineRange(hunk.Target["lines=".Length..]);
            ValidateRange(lines, start, end, hunk);
            var result = new List<string>(lines);
            result.RemoveRange(start - 1, end - start + 1);
            return result;
        }

        if (hunk.Target.StartsWith("pattern="))
        {
            var pattern = UnquotePattern(hunk.Target["pattern=".Length..]);
            if (hunk.All)
                return lines.Where(l =>
                    !l.Contains(pattern, StringComparison.Ordinal)).ToList();

            var idx    = FindFirst(lines, pattern, hunk);
            var result = new List<string>(lines);
            result.RemoveAt(idx);
            return result;
        }

        throw new DxException(DxError.ParseError,
            $"Unrecognized delete target: {hunk.Target}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Splits a string into a mutable list of lines, normalised to <c>\n</c>.</summary>
    private static List<string> SplitLines(string text)
        => text.ReplaceLineEndings("\n")
               .Split('\n')
               .ToList();

    /// <summary>
    /// Parses a line-range specification in the form <c>N-M</c> into start and end
    /// 1-based line numbers.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> when the format is invalid.
    /// </exception>
    private static (int Start, int End) ParseLineRange(string spec)
    {
        var parts = spec.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var s)
            || !int.TryParse(parts[1], out var e))
            throw new DxException(DxError.ParseError,
                $"Invalid line range: {spec}");
        return (s, e);
    }

    /// <summary>Parses a single 1-based line number from a string.</summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> when the value is not a valid integer.
    /// </exception>
    private static int ParseSingleLine(string spec)
    {
        if (!int.TryParse(spec, out var n))
            throw new DxException(DxError.ParseError,
                $"Invalid line number: {spec}");
        return n;
    }

    /// <summary>Strips surrounding quotes from a pattern string.</summary>
    private static string UnquotePattern(string raw)
        => raw.Trim().Trim('"');

    /// <summary>
    /// Finds the zero-based index of the first line containing <paramref name="pattern"/>.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> when no line matches.
    /// </exception>
    private static int FindFirst(List<string> lines, string pattern, PatchHunk hunk)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains(pattern, StringComparison.Ordinal))
                return i;

        throw new DxException(DxError.ParseError,
            $"Patch precondition failed: pattern \"{pattern}\" not found " +
            $"(op={hunk.Operation})");
    }

    /// <summary>
    /// Validates that the specified 1-based line range is within the bounds of
    /// <paramref name="lines"/>.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> when the range is out of bounds.
    /// </exception>
    private static void ValidateRange(
        List<string> lines, int start, int end, PatchHunk hunk)
    {
        if (start < 1 || end < start || end > lines.Count)
            throw new DxException(DxError.ParseError,
                $"Patch precondition failed: line range {start}-{end} " +
                $"out of bounds (file has {lines.Count} lines)");
    }

    /// <summary>
    /// Validates that the specified 1-based line number is within the bounds of
    /// <paramref name="lines"/>.
    /// </summary>
    /// <exception cref="DxException">
    /// Thrown with <see cref="DxError.ParseError"/> when the line number is out of bounds.
    /// </exception>
    private static void ValidateLine(List<string> lines, int lineNo, PatchHunk hunk)
    {
        if (lineNo < 1 || lineNo > lines.Count)
            throw new DxException(DxError.ParseError,
                $"Patch precondition failed: line {lineNo} " +
                $"out of bounds (file has {lines.Count} lines)");
    }
}
