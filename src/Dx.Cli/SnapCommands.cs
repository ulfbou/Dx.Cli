using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── Shared snap settings ──────────────────────────────────────────────────────

public class SnapBaseSettings : CommandSettings
{
    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--session <id>")]
    public string? Session { get; init; }
}

// ── dx snap list ─────────────────────────────────────────────────────────────

public sealed class SnapListSettings : SnapBaseSettings { }

public sealed class SnapListCommand : DxCommandBase<SnapListSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapListSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);
            var snaps = runtime.ListSnaps();
            var head = runtime.GetHead();

            var tree = new Tree("[bold]Snap graph[/]");

            foreach (var snap in snaps)
            {
                var marker = snap.IsHead ? " [green]← HEAD[/]" : "";
                var ts = snap.CreatedUtc.Length > 19
                    ? snap.CreatedUtc[..19].Replace('T', ' ')
                    : snap.CreatedUtc;

                tree.AddNode(
                    $"[yellow]{snap.Handle}[/]  [dim]{ts}[/]{marker}");
            }

            AnsiConsole.Write(tree);
            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dx snap show ─────────────────────────────────────────────────────────────

public sealed class SnapShowSettings : SnapBaseSettings
{
    [CommandArgument(0, "<handle>")]
    [Description("Snap handle, e.g. T0003")]
    public string Handle { get; init; } = "";

    [CommandOption("--files")]
    [Description("List all files in the snap.")]
    public bool Files { get; init; }
}

public sealed class SnapShowCommand : DxCommandBase<SnapShowSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapShowSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);

            AnsiConsole.MarkupLine($"[yellow]{s.Handle}[/]");

            if (s.Files)
            {
                var files = runtime.GetSnapFiles(s.Handle);
                var table = new Table()
                    .Border(TableBorder.Simple)
                    .AddColumn("Path")
                    .AddColumn(new TableColumn("Size").RightAligned());

                foreach (var f in files)
                    table.AddRow(f.Path, $"{f.SizeBytes:N0}");

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"[dim]{files.Count} file(s)[/]");
            }

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dx snap diff ─────────────────────────────────────────────────────────────

public sealed class SnapDiffSettings : SnapBaseSettings
{
    [CommandArgument(0, "<from>")]
    public string From { get; init; } = "";

    [CommandArgument(1, "<to>")]
    public string To { get; init; } = "";

    [CommandOption("--path <filter>")]
    [Description("Scope diff to a specific path prefix.")]
    public string? Path { get; init; }
}

public sealed class SnapDiffCommand : DxCommandBase<SnapDiffSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapDiffSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);
            var entries = runtime.Diff(s.From, s.To, s.Path);

            if (entries.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No differences.[/]");
                return Task.FromResult(0);
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Status")
                .AddColumn("Path");

            foreach (var e in entries)
            {
                var (label, color) = e.Status switch
                {
                    DiffStatus.Added => ("added", "green"),
                    DiffStatus.Deleted => ("deleted", "red"),
                    DiffStatus.Modified => ("modified", "yellow"),
                    _ => ("?", "dim"),
                };
                table.AddRow($"[{color}]{label}[/]", e.Path);
            }

            AnsiConsole.MarkupLine(
                $"[bold]diff[/] [yellow]{s.From}[/] → [yellow]{s.To}[/]");
            AnsiConsole.Write(table);

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dx snap checkout ─────────────────────────────────────────────────────────

public sealed class SnapCheckoutSettings : SnapBaseSettings
{
    [CommandArgument(0, "<handle>")]
    [Description("Snap to restore working tree to.")]
    public string Handle { get; init; } = "";
}

public sealed class SnapCheckoutCommand : DxCommandBase<SnapCheckoutSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapCheckoutSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);

            string newHandle = null!;

            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start($"Checking out {s.Handle}...", _ =>
                {
                    newHandle = runtime.Checkout(s.Handle);
                });

            AnsiConsole.MarkupLine(
                $"[green]Checked out[/] [yellow]{s.Handle}[/] → [yellow]{newHandle}[/]");

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}
