using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── dxs log ───────────────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs log</c> command, including optional session
/// filtering and an entry-count limit.
/// </summary>
public sealed class LogSettings : CommandSettings
{
    /// <summary>
    /// Gets the session identifier to filter the log by.
    /// When omitted, the log for the most recent active session is displayed.
    /// </summary>
    [CommandOption("-s|--session <id>")]
    [Description("Show log for a specific session. Defaults to the most recent active session.")]
    public string? Session { get; init; }

    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }

    /// <summary>
    /// Gets the maximum number of log entries to display, ordered from most recent to oldest.
    /// Defaults to <c>20</c>.
    /// </summary>
    [CommandOption("-n|--limit <count>")]
    [Description("Maximum number of log entries to display, newest first.")]
    [DefaultValue(20)]
    public int Limit { get; init; }
}

/// <summary>
/// Implements the <c>dxs log</c> command, which retrieves and displays a reverse-chronological
/// history of transaction events for the current or specified session.
/// </summary>
/// <remarks>
/// Each log entry records the transaction direction (<c>llm</c> or <c>tool</c>), the resulting
/// snapshot handle (if the transaction mutated state), and whether the transaction succeeded.
/// Both successful and failed transactions are included in the output.
/// </remarks>
public sealed class LogCommand : DxCommandBase<LogSettings>
{
    /// <inheritdoc />
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
