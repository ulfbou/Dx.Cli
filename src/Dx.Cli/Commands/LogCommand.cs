using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

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
