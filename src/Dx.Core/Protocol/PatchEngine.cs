namespace Dx.Core.Protocol;

/// <summary>
/// Applies PatchHunk operations to file content.
/// All hunks must succeed or a PatchException is thrown (caller handles rollback).
/// </summary>
public static class PatchEngine
{
    public static string Apply(string content, IReadOnlyList<PatchHunk> hunks)
    {
        var lines = SplitLines(content);

        foreach (var hunk in hunks)
            lines = ApplyHunk(lines, hunk);

        return string.Join(Environment.NewLine, lines)
               + (content.EndsWith('\n') ? Environment.NewLine : "");
    }

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

    // ── replace ────────────────────────────────────────────────────────────

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
                var result = lines.SelectMany(l =>
                    l.Contains(pattern, StringComparison.Ordinal)
                        ? bodyLines
                        : [l]).ToList();
                return result;
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

    // ── insert ─────────────────────────────────────────────────────────────

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

    // ── delete ─────────────────────────────────────────────────────────────

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

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<string> SplitLines(string text)
        => text.ReplaceLineEndings("\n")
               .Split('\n')
               .ToList();

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

    private static int ParseSingleLine(string spec)
    {
        if (!int.TryParse(spec, out var n))
            throw new DxException(DxError.ParseError,
                $"Invalid line number: {spec}");
        return n;
    }

    private static string UnquotePattern(string raw)
        => raw.Trim().Trim('"');

    private static int FindFirst(List<string> lines, string pattern, PatchHunk hunk)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains(pattern, StringComparison.Ordinal))
                return i;

        throw new DxException(DxError.ParseError,
            $"Patch precondition failed: pattern \"{pattern}\" not found " +
            $"(op={hunk.Operation})");
    }

    private static void ValidateRange(
        List<string> lines, int start, int end, PatchHunk hunk)
    {
        if (start < 1 || end < start || end > lines.Count)
            throw new DxException(DxError.ParseError,
                $"Patch precondition failed: line range {start}-{end} " +
                $"out of bounds (file has {lines.Count} lines)");
    }

    private static void ValidateLine(List<string> lines, int lineNo, PatchHunk hunk)
    {
        if (lineNo < 1 || lineNo > lines.Count)
            throw new DxException(DxError.ParseError,
                $"Patch precondition failed: line {lineNo} " +
                $"out of bounds (file has {lines.Count} lines)");
    }
}
