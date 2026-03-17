using Dx.Core;
using Dx.Core.Protocol;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

public sealed class ApplySettings : CommandSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Path to .dx document, or '-' to read from stdin.")]
    public string File { get; init; } = "";

    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--session <id>")]
    public string? Session { get; init; }

    [CommandOption("--dry-run")]
    [Description("Validate and show what would be applied without executing.")]
    public bool DryRun { get; init; }

    [CommandOption("--verbose")]
    public bool Verbose { get; init; }
}

public sealed class ApplyCommand : DxCommandBase<ApplySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, ApplySettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session,
                new ConsoleDxLogger(s.Verbose));

            // Read document
            string text;
            if (s.File == "-")
                text = await Console.In.ReadToEndAsync();
            else
                text = await System.IO.File.ReadAllTextAsync(s.File);

            // Parse
            var (doc, errors) = DxParser.ParseText(text);

            if (errors.Count > 0)
            {
                foreach (var e in errors)
                    AnsiConsole.MarkupLine(
                        $"[red]parse error[/] line {e.Line}: {e.Message}");
                return 2;
            }

            if (doc is null)
            {
                AnsiConsole.MarkupLine("[red]error:[/] Failed to parse document.");
                return 2;
            }

            if (s.DryRun)
            {
                AnsiConsole.MarkupLine("[yellow]dry run[/] — no changes applied.");
                RenderDocumentSummary(doc);
                return 0;
            }

            // Apply with progress
            DispatchResult result = null!;

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx2 =>
                {
                    var task = ctx2.AddTask("Applying...", maxValue: 100);
                    var progress = new Progress<string>(msg =>
                    {
                        task.Description = msg;
                        task.Increment(10);
                    });

                    result = await runtime.ApplyAsync(doc, dryRun: false, progress);
                });

            RenderApplyResult(result, doc);

            return result.Success ? 0 : 1;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    private static void RenderApplyResult(DispatchResult result, DxDocument doc)
    {
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Transaction failed:[/] {result.Error}");
            AnsiConsole.MarkupLine("[dim]Working tree rolled back.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Block")
            .AddColumn("Path")
            .AddColumn("Result");

        foreach (var op in result.Operations)
        {
            var status = op.Success
                ? "[green]ok[/]"
                : "[red]failed[/]";
            table.AddRow(
                op.BlockType,
                op.Path ?? "[dim]—[/]",
                op.Detail is not null ? $"{status} {op.Detail}" : status);
        }

        AnsiConsole.Write(table);

        if (result.NewHandle is not null)
            AnsiConsole.MarkupLine($"[green]→[/] [yellow]{result.NewHandle}[/]");
        else
            AnsiConsole.MarkupLine("[dim]No changes (no-op).[/]");
    }

    private static void RenderDocumentSummary(DxDocument doc)
    {
        AnsiConsole.MarkupLine($"  Session: [cyan]{doc.Header.Session ?? "(none)"}[/]");
        AnsiConsole.MarkupLine($"  Base:    [yellow]{doc.Header.Base ?? "(none)"}[/]");
        AnsiConsole.MarkupLine($"  Blocks:  {doc.Blocks.Count}");
        AnsiConsole.MarkupLine($"  Mutating: {doc.IsMutating}");
    }
}
