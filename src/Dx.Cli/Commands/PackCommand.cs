using Dx.Core;
using Dx.Core.Genesis;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Text;

namespace Dx.Cli.Commands;

/// <summary>
/// Defines the settings for the <c>dxs pack</c> command, which serialises one or more
/// workspace files into a read-only DX document suitable for use as LLM context.
/// </summary>
public sealed class PackSettings : CommandSettings
{
    /// <summary>
    /// Gets the file or directory to pack.
    /// When a directory is specified all text files within it are included recursively,
    /// subject to the built-in exclusion list. When a single file is specified only that
    /// file is packed. Defaults to <c>.</c> (the current directory).
    /// </summary>
    [CommandArgument(0, "<path>")]
    [Description("File or directory to pack. Defaults to the current directory.")]
    public string Path { get; init; } = ".";

    /// <summary>
    /// Gets an optional session identifier used to resolve the ignore set for file exclusion.
    /// When omitted, the most recent active session is used, which is usually what you want
    /// when running the command from a workspace context. 
    /// </summary>
    [CommandOption("-s|--session <id>")]
    [Description("Target session identifier (used to resolve IgnoreSet). Defaults to most recent active session.")]
    public string? Session { get; init; }

    /// <summary>
    /// Gets the explicit workspace root path used to compute relative file paths in the
    /// generated DX document. When omitted, the root is discovered automatically.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root used for relative path computation.")]
    public string? Root { get; init; }

    /// <summary>
    /// Gets the output file path.
    /// When omitted, the packed document is written to standard output,
    /// making it easy to pipe directly to another tool or command.
    /// </summary>
    [CommandOption("-o|--out <file>")]
    [Description("Output file path. Omit to write to stdout.")]
    public string? Out { get; init; }

    /// <summary>
    /// Gets a value indicating whether a <c>%%DX</c> session header line should be
    /// prepended to the output, making the document a valid standalone DX document.
    /// </summary>
    [CommandOption("--no-header")]
    [Description("Do not emit the %%DX header and %%END footer.")]
    public bool NoHeader { get; init; }

    /// <summary>
    /// Gets a value indicating whether a directory tree overview should be prepended
    /// to the output as a <c>%%NOTE</c> block, giving the reader structural context.
    /// </summary>
    [CommandOption("--tree")]
    [Description("Prepend a directory tree overview as a %%NOTE block.")]
    public bool Tree { get; init; }

    /// <summary>
    /// Gets an optional file extension filter (e.g. <c>.cs</c>).
    /// When specified, only files matching the extension are included in the output.
    /// </summary>
    [CommandOption("-f|--file-type <ext>")]
    [Description("Include only files with this extension, e.g. .cs")]
    public string? FileType { get; init; }

    /// <summary>
    /// Gets an optional line-range specification in the format <c>path:N-M</c>.
    /// When specified, only lines <c>N</c> through <c>M</c> of the matched file are included.
    /// </summary>
    [CommandOption("--lines <spec>")]
    [Description("Include only the specified line range. Format: relative/path:N-M")]
    public string? Lines { get; init; }

    /// <summary>
    /// Gets a value indicating whether a <c>%%NOTE</c> metadata block containing the file
    /// path, size, and line count should be emitted immediately before each <c>%%FILE</c> block.
    /// </summary>
    [CommandOption("-m|--metadata")]
    [Description("Emit a %%NOTE metadata block (path, size, line count) before each %%FILE block.")]
    public bool Metadata { get; init; }
}

/// <summary>
/// Implements the <c>dxs pack</c> command, which reads workspace files and serialises them
/// into a structured DX document formatted for consumption as LLM context.
/// </summary>
/// <remarks>
/// <para>
/// Each eligible file is emitted as a <c>%%FILE path="..." readonly="true"</c> block with
/// its content indented by four spaces, followed by <c>%%ENDBLOCK</c>.
/// </para>
/// <para>
/// File inclusion and exclusion is determined exclusively by the persisted
/// <see cref="Dx.Core.IgnoreSet"/> associated with the target session. No default
/// rules, heuristics, or policy evaluation are performed at runtime.
/// </para>
/// <para>
/// Binary files and files excluded by the session's ignore set are silently skipped.
/// </para>
/// <para>
/// The output is always written as UTF-8 without a BOM, regardless of the source file encoding.
/// </para>
/// </remarks>
public sealed class PackCommand : DxCommandBase<PackSettings>
{
    /// <summary>
    /// File extensions whose content is never text and should be skipped without
    /// attempting a UTF-8 decode.
    /// </summary>
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

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext ctx, PackSettings s)
    {
        try
        {

            var root = FindRoot(s.Root);

            IgnoreSet ignoreSet;

            if (DxRuntime.IsWorkspace(root))
            {
                // Workspace mode
                var runtime = DxRuntime.Open(root, s.Session);
                ignoreSet = runtime.IgnoreSet;
            }
            else
            {
                // Stateless mode (workspace not initialized)
                ignoreSet = IgnoreSetFactory.Create(
                    artifactsDir: null,
                    userExcludes: Enumerable.Empty<string>(),
                    includeBuildOutput: false);
            }

            var sb = new StringBuilder();

            if (!s.NoHeader)
            {
                // TODO: consider allowing custom session and author via command options
                sb.AppendLine("%%DX v1.3 author=tool"); 
            }

            var targetAbs = Path.IsPathRooted(s.Path)
                ? s.Path
                : Path.GetFullPath(Path.Combine(root, s.Path));

            if (s.Tree)
            {
                sb.AppendLine("%%NOTE");
                AppendTree(sb, targetAbs, root, prefix: "    ");
                sb.AppendLine("%%ENDBLOCK");
                sb.AppendLine();
            }


            // Collect files (raw enumeration, filtered by ignoreSet)
            var files = File.Exists(targetAbs)
                ? (new[] { targetAbs }).ToList()
                : Directory
                    .EnumerateFiles(targetAbs, "*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                        return !ignoreSet.IsExcluded(rel);
                    })
                    .OrderBy(f => f)
                    .ToList();

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
                string rawContent;
                try
                {
                    rawContent = await ReadUtf8Async(file);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (s.FileType is not null)
                {
                    if (!relPath.EndsWith(s.FileType, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var args = $"path=\"{relPath}\" readonly=\"true\"";

                string content;

                if (lineRanges is not null &&
                    lineRanges.TryGetValue(relPath, out var range))
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

            if (!s.NoHeader)
            {
                sb.AppendLine("%%END");
            }

            var output = sb.ToString();

            if (s.Out is not null)
            {
                await File.WriteAllTextAsync(s.Out, output, new UTF8Encoding(false));
                AnsiConsole.MarkupLine(
                    $"[green]Packed[/] {packed} file(s) → [dim]{s.Out}[/]" +
                    (skipped > 0 ? $" [dim]({skipped} skipped)[/]" : ""));
            }
            else
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(output);

                if (skipped > 0)
                    Console.Error.WriteLine($"pack: {skipped} file(s) skipped (binary or unreadable)");
            }

            return 0;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    private static async Task<string> ReadUtf8Async(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);

        var sampleLen = Math.Min(bytes.Length, 8192);
        var suspicious = 0;
        for (var i = 0; i < sampleLen; i++)
        {
            var b = bytes[i];
            if (b == 0x00 || (b < 0x09) || (b > 0x0D && b < 0x20 && b != 0x1B))
                suspicious++;
        }
        if (sampleLen > 0 && (double)suspicious / sampleLen > 0.01)
            throw new InvalidDataException();

        var offset = (bytes.Length >= 3 &&
                      bytes[0] == 0xEF &&
                      bytes[1] == 0xBB &&
                      bytes[2] == 0xBF) ? 3 : 0;

        return new UTF8Encoding(false, true)
            .GetString(bytes, offset, bytes.Length - offset);
    }

    private static void AppendTree(
        StringBuilder sb, string dir, string root, string prefix, int depth = 0)
    {
        if (depth > 6) return;

        var dirName = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(dirName)) return;

        sb.AppendLine($"{prefix}{dirName}/");

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d))
                AppendTree(sb, sub, root, prefix + "  ", depth + 1);

            foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f))
            {
                var ext = Path.GetExtension(f);
                if (!BinaryExtensions.Contains(ext))
                    sb.AppendLine($"{prefix}  {Path.GetFileName(f)}");
            }
        }
        catch (UnauthorizedAccessException) { }
    }
}
