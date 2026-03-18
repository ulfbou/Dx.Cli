using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Text;

namespace Dx.Cli.Commands;

public sealed class PackSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("File or directory to pack.")]
    public string Path { get; init; } = ".";

    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--out <file>")]
    [Description("Output file. Omit to write to stdout.")]
    public string? Out { get; init; }

    [CommandOption("--session-header")]
    [Description("Include %%DX session header in output.")]
    public bool SessionHeader { get; init; }

    [CommandOption("--tree")]
    [Description("Prepend a directory tree overview.")]
    public bool Tree { get; init; }

    [CommandOption("--file-type <ext>")]
    [Description("Filter by extension, e.g. .cs")]
    public string? FileType { get; init; }

    [CommandOption("--lines <spec>")]
    [Description("Include only specified line ranges. Format: path:N-M")]
    public string? Lines { get; init; }

    [CommandOption("--metadata")]
    [Description("Include metadata block per file.")]
    public bool Metadata { get; init; }
}

public sealed class PackCommand : DxCommandBase<PackSettings>
{
    // Directories excluded from pack regardless of depth in tree.
    // Matched against any path segment, not just the root prefix.
    private static readonly HashSet<string> ExcludedDirSegments = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".dx", ".git", ".github", ".hg", ".svn", ".vs", ".vscode", ".idea",
        "node_modules", "bin", "obj",
    };

    // Extensions that are never text — skip without attempting to read.
    private static readonly HashSet<string> BinaryExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".db-wal", ".db-shm",
        ".exe", ".dll", ".pdb", ".so", ".dylib",
        ".zip", ".tar", ".gz", ".7z", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp",
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".bin", ".obj", ".lib", ".a",
        ".wasm",
    };

    public override async Task<int> ExecuteAsync(CommandContext ctx, PackSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var sb = new StringBuilder();

            if (s.SessionHeader)
                sb.AppendLine("%%DX v1.3 author=tool");

            var targetAbs = System.IO.Path.IsPathRooted(s.Path)
                ? s.Path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, s.Path));

            // Tree block
            if (s.Tree)
            {
                sb.AppendLine("%%NOTE");
                AppendTree(sb, targetAbs, root, prefix: "    ");
                sb.AppendLine("%%ENDBLOCK");
                sb.AppendLine();
            }

            // Collect files
            var files = File.Exists(targetAbs)
                ? [targetAbs]
                : Directory
                    .EnumerateFiles(targetAbs, "*", SearchOption.AllDirectories)
                    .Where(f => !IsExcluded(f, root))
                    .Where(f => s.FileType is null ||
                                f.EndsWith(s.FileType, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .ToList();

            // Parse line range filter
            Dictionary<string, (int Start, int End)>? lineRanges = null;
            if (s.Lines is not null)
            {
                lineRanges = new(StringComparer.OrdinalIgnoreCase);
                var parts = s.Lines.Split(':');
                if (parts.Length == 2 && parts[1].Contains('-'))
                {
                    var rangeParts = parts[1].Split('-');
                    if (int.TryParse(rangeParts[0], out var rs)
                        && int.TryParse(rangeParts[1], out var re))
                        lineRanges[parts[0]] = (rs, re);
                }
            }

            var packed = 0;
            var skipped = 0;

            foreach (var file in files)
            {
                // Attempt to read as UTF-8; skip if binary or unreadable
                string rawContent;
                try
                {
                    rawContent = await ReadUtf8Async(file);
                }
                catch (InvalidDataException)
                {
                    skipped++;
                    continue; // non-text file, silently skip
                }
                catch (IOException)
                {
                    skipped++;
                    continue; // locked or unreadable, silently skip
                }

                var relPath = System.IO.Path.GetRelativePath(root, file)
                    .Replace('\\', '/');

                var args = $"path=\"{relPath}\" readonly=\"true\"";

                string content;

                if (lineRanges is not null
                    && lineRanges.TryGetValue(relPath, out var range))
                {
                    var allLines = rawContent.ReplaceLineEndings("\n").Split('\n');
                    var start = Math.Max(1, range.Start);
                    var end = Math.Min(allLines.Length, range.End);
                    content = string.Join("\n",
                        allLines[(start - 1)..end].Select(l => "    " + l));
                    args += $" lines=\"{start}-{end}\"";
                }
                else
                {
                    content = string.Join("\n",
                        rawContent.ReplaceLineEndings("\n")
                                  .Split('\n')
                                  .Select(l => "    " + l));
                }

                if (s.Metadata)
                {
                    var info = new FileInfo(file);
                    var lineCount = rawContent.ReplaceLineEndings("\n").Count(c => c == '\n') + 1;
                    sb.AppendLine("%%NOTE");
                    sb.AppendLine($"    path:   {relPath}");
                    sb.AppendLine($"    size:   {info.Length:N0} bytes");
                    sb.AppendLine($"    lines:  {lineCount}");
                    sb.AppendLine("%%ENDBLOCK");
                }

                sb.AppendLine($"%%FILE {args}");
                sb.AppendLine(content);
                sb.AppendLine("%%ENDBLOCK");
                sb.AppendLine();

                packed++;
            }

            if (s.SessionHeader)
                sb.AppendLine("%%END");

            var output = sb.ToString();

            if (s.Out is not null)
            {
                // Always write the pack file as UTF-8 no-BOM
                await File.WriteAllTextAsync(s.Out, output, new UTF8Encoding(false));
                AnsiConsole.MarkupLine(
                    $"[green]Packed[/] {packed} file(s) → [dim]{s.Out}[/]" +
                    (skipped > 0 ? $" [dim]({skipped} skipped)[/]" : ""));
            }
            else
            {
                // stdout: ensure UTF-8 output
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(output);

                if (skipped > 0)
                    AnsiConsole.MarkupLine(
                        $"[dim]{skipped} file(s) skipped (binary or unreadable)[/]");
            }

            return 0;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    /// <summary>
    /// Returns true if the file should be excluded from packing.
    /// Checks every path segment against the excluded set, so nested
    /// .dx/ and .git/ directories are caught regardless of depth.
    /// </summary>
    private static bool IsExcluded(string absolutePath, string root)
    {
        var rel = System.IO.Path.GetRelativePath(root, absolutePath);
        var segments = rel.Replace('\\', '/').Split('/');

        // Any segment matching an excluded directory name → skip
        // (covers subfolder/.dx/dx.db, deep/node_modules/x.js, etc.)
        foreach (var segment in segments.SkipLast(1)) // skip last = filename
            if (ExcludedDirSegments.Contains(segment))
                return true;

        // Binary extension check
        var ext = System.IO.Path.GetExtension(absolutePath);
        if (BinaryExtensions.Contains(ext))
            return true;

        return false;
    }

    /// <summary>
    /// Reads a file as UTF-8, stripping BOM if present.
    /// Throws InvalidDataException if the content is not valid UTF-8.
    /// </summary>
    private static async Task<string> ReadUtf8Async(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);

        // Quick binary sniff: if more than 1% of the first 8KB are null bytes
        // or non-printable control chars (excluding common whitespace), treat as binary.
        var sampleLen = Math.Min(bytes.Length, 8192);
        var suspicious = 0;
        for (var i = 0; i < sampleLen; i++)
        {
            var b = bytes[i];
            if (b == 0x00 || (b < 0x09) || (b > 0x0D && b < 0x20 && b != 0x1B))
                suspicious++;
        }
        if (sampleLen > 0 && (double)suspicious / sampleLen > 0.01)
            throw new InvalidDataException("File appears to be binary.");

        // Strip BOM if present (UTF-8 BOM: EF BB BF)
        var offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            offset = 3;

        try
        {
            // Strict UTF-8 decode — throws on invalid sequences
            return new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("File is not valid UTF-8.");
        }
    }

    private static void AppendTree(
        StringBuilder sb, string dir, string root, string prefix, int depth = 0)
    {
        if (depth > 6) return;

        var dirName = System.IO.Path.GetFileName(dir);
        if (string.IsNullOrEmpty(dirName)) return;

        if (ExcludedDirSegments.Contains(dirName)) return;

        sb.AppendLine($"{prefix}{dirName}/");

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d))
                AppendTree(sb, sub, root, prefix + "  ", depth + 1);

            foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f))
            {
                var ext = System.IO.Path.GetExtension(f);
                if (!BinaryExtensions.Contains(ext))
                    sb.AppendLine($"{prefix}  {System.IO.Path.GetFileName(f)}");
            }
        }
        catch (UnauthorizedAccessException) { /* skip inaccessible */ }
    }
}
