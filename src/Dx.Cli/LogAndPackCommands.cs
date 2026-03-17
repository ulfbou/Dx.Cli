using Dx.Core;
using Dx.Core.Protocol;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Text;

namespace Dx.Cli.Commands;

// ── dx log ────────────────────────────────────────────────────────────────────

public sealed class LogSettings : CommandSettings
{
    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--session <id>")]
    public string? Session { get; init; }

    [CommandOption("--limit <n>")]
    [DefaultValue(50)]
    public int Limit { get; init; } = 50;
}

public sealed class LogCommand : DxCommandBase<LogSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, LogSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);
            var entries = runtime.GetLog(s.Limit);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("#")
                .AddColumn("Dir")
                .AddColumn("Snap")
                .AddColumn("OK")
                .AddColumn("Time");

            foreach (var e in entries)
            {
                var snap = e.SnapHandle is not null
                    ? $"[yellow]{e.SnapHandle}[/]"
                    : "[dim]—[/]";
                var ok = e.TxSuccess == 1
                    ? "[green]✓[/]"
                    : "[red]✗[/]";
                var ts = e.CreatedAt.Length > 19
                    ? e.CreatedAt[..19].Replace('T', ' ')
                    : e.CreatedAt;

                table.AddRow(e.Id.ToString(), e.Direction, snap, ok, $"[dim]{ts}[/]");
            }

            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dx pack ───────────────────────────────────────────────────────────────────

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
    public override async Task<int> ExecuteAsync(CommandContext ctx, PackSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var sb = new StringBuilder();

            if (s.SessionHeader)
                sb.AppendLine("%%DX v1.3 author=tool");

            // Tree block
            if (s.Tree)
            {
                sb.AppendLine("%%NOTE");
                var targetPath = Path.IsPathRooted(s.Path)
                    ? s.Path
                    : Path.GetFullPath(Path.Combine(root, s.Path));
                AppendTree(sb, targetPath, root, prefix: "    ");
                sb.AppendLine("%%ENDBLOCK");
                sb.AppendLine();
            }

            // Collect files
            var targetAbs = Path.IsPathRooted(s.Path)
                ? s.Path
                : Path.GetFullPath(Path.Combine(root, s.Path));

            var files = File.Exists(targetAbs)
                ? [targetAbs]
                : Directory.EnumerateFiles(targetAbs, "*", SearchOption.AllDirectories)
                           .Where(f => s.FileType is null ||
                                       f.EndsWith(s.FileType, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(f => f)
                           .ToList();

            // Parse line range filter
            Dictionary<string, (int Start, int End)>? lineRanges = null;
            if (s.Lines is not null)
            {
                lineRanges = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
                var parts = s.Lines.Split(':');
                if (parts.Length == 2 && parts[1].Contains('-'))
                {
                    var rangeParts = parts[1].Split('-');
                    if (int.TryParse(rangeParts[0], out var rs)
                        && int.TryParse(rangeParts[1], out var re))
                        lineRanges[parts[0]] = (rs, re);
                }
            }

            foreach (var file in files)
            {
                var relPath = DxPath.Normalize(root, file);

                // Skip .dx internals
                if (relPath.StartsWith(".dx/", StringComparison.Ordinal)) continue;

                var args = $"path=\"{relPath}\" readonly=\"true\"";

                string content;

                if (lineRanges is not null
                    && lineRanges.TryGetValue(relPath, out var range))
                {
                    var allLines = await File.ReadAllLinesAsync(file);
                    var start = Math.Max(1, range.Start);
                    var end = Math.Min(allLines.Length, range.End);
                    content = string.Join("\n",
                        allLines[(start - 1)..end]
                            .Select(l => "    " + l));
                    args += $" lines=\"{start}-{end}\"";
                }
                else
                {
                    var raw = await File.ReadAllTextAsync(file);
                    content = string.Join("\n",
                        raw.ReplaceLineEndings("\n")
                           .Split('\n')
                           .Select(l => "    " + l));
                }

                if (s.Metadata)
                {
                    var info = new FileInfo(file);
                    var lineCount = (await File.ReadAllLinesAsync(file)).Length;
                    sb.AppendLine($"%%NOTE");
                    sb.AppendLine($"    path:   {relPath}");
                    sb.AppendLine($"    size:   {info.Length:N0} bytes");
                    sb.AppendLine($"    lines:  {lineCount}");
                    sb.AppendLine("%%ENDBLOCK");
                }

                sb.AppendLine($"%%FILE {args}");
                sb.AppendLine(content);
                sb.AppendLine("%%ENDBLOCK");
                sb.AppendLine();
            }

            if (s.SessionHeader)
                sb.AppendLine("%%END");

            var output = sb.ToString();

            if (s.Out is not null)
            {
                await File.WriteAllTextAsync(s.Out, output);
                AnsiConsole.MarkupLine($"[green]Packed[/] {files.Count} file(s) → [dim]{s.Out}[/]");
            }
            else
            {
                Console.Write(output); // stdout for piping
            }

            return 0;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    private static void AppendTree(
        StringBuilder sb, string dir, string root, string prefix, int depth = 0)
    {
        if (depth > 4) return;

        var relDir = DxPath.Normalize(root, dir);
        sb.AppendLine($"{prefix}{Path.GetFileName(dir)}/");

        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d))
        {
            var rel = DxPath.Normalize(root, sub);
            if (rel.StartsWith(".dx/") || rel.StartsWith(".git/")) continue;
            AppendTree(sb, sub, root, prefix + "  ", depth + 1);
        }

        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f))
            sb.AppendLine($"{prefix}  {Path.GetFileName(f)}");
    }
}
