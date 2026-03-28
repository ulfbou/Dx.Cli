using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── Shared snap settings ──────────────────────────────────────────────────────

/// <summary>
/// Provides shared base settings for all <c>dxs snap</c> sub-commands, defining the
/// workspace root and session scope.
/// </summary>
public class SnapBaseSettings : CommandSettings
{
    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }

    /// <summary>
    /// Gets the session identifier to target.
    /// When omitted, the most recent active session is used.
    /// </summary>
    [CommandOption("-s|--session <id>")]
    [Description("Target session identifier. Defaults to the most recent active session.")]
    public string? Session { get; init; }
}

// ── dxs snap list ─────────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs snap list</c> command.
/// Inherits workspace root and session scope from <see cref="SnapBaseSettings"/>.
/// </summary>
public sealed class SnapListSettings : SnapBaseSettings { }

/// <summary>
/// Implements the <c>dxs snap list</c> command, which renders the snapshot graph for the
/// current session in chronological order, annotating the current HEAD.
/// </summary>
public sealed class SnapListCommand : DxCommandBase<SnapListSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapListSettings s)
    {
        try
        {
            var root    = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);
            var snaps   = runtime.ListSnaps();
            var head    = runtime.GetHead();

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
        catch (Exception ex)   { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs snap show ─────────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs snap show</c> command.
/// </summary>
public sealed class SnapShowSettings : SnapBaseSettings
{
    /// <summary>
    /// Gets the handle of the snapshot to inspect (e.g. <c>T0003</c>).
    /// </summary>
    [CommandArgument(0, "<handle>")]
    [Description("Snap handle to inspect (e.g. T0003).")]
    public string Handle { get; init; } = "";

    /// <summary>
    /// Gets a value indicating whether the full file manifest should be listed.
    /// When set, a table of all tracked paths and their sizes is printed below the
    /// snapshot summary.
    /// </summary>
    [CommandOption("-f|--files")]
    [Description("List all files tracked in the snapshot.")]
    public bool Files { get; init; }
}

/// <summary>
/// Implements the <c>dxs snap show</c> command, which displays metadata for a specific
/// snapshot and, optionally, its complete file manifest.
/// </summary>
public sealed class SnapShowCommand : DxCommandBase<SnapShowSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapShowSettings s)
    {
        try
        {
            var root    = FindRoot(s.Root);
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
        catch (Exception ex)   { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs snap diff ─────────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs snap diff</c> command.
/// </summary>
public sealed class SnapDiffSettings : SnapBaseSettings
{
    /// <summary>
    /// Gets the handle of the snapshot to diff from (the older / baseline state).
    /// </summary>
    [CommandArgument(0, "<from>")]
    [Description("Baseline snap handle (e.g. T0000).")]
    public string From { get; init; } = "";

    /// <summary>
    /// Gets the handle of the snapshot to diff to (the newer / candidate state).
    /// </summary>
    [CommandArgument(1, "<to>")]
    [Description("Candidate snap handle (e.g. T0005).")]
    public string To { get; init; } = "";

    /// <summary>
    /// Gets an optional path prefix used to scope the diff to a specific subdirectory
    /// or file. Only entries whose paths begin with this prefix are included in the output.
    /// </summary>
    [CommandOption("-p|--path <filter>")]
    [Description("Scope the diff to paths beginning with this prefix (e.g. src/).")]
    public string? Path { get; init; }
}

/// <summary>
/// Implements the <c>dxs snap diff</c> command, which computes and displays the file-level
/// differences between two snapshots.
/// </summary>
/// <remarks>
/// Each entry is classified as <c>added</c>, <c>deleted</c>, or <c>modified</c>.
/// Unchanged files are not shown. When no differences are found, a brief message is
/// printed and the command exits with code <c>0</c>.
/// </remarks>
public sealed class SnapDiffCommand : DxCommandBase<SnapDiffSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, SnapDiffSettings s)
    {
        try
        {
            var root    = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);
            var entries = runtime.Diff(s.From, s.To, s.Path);

            if (entries.Count == 0)
            {
                // Stable wording — survives both TTY (ANSI) and non-TTY capture
                AnsiConsole.MarkupLine("[dim]No differences found.[/]");
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
                    DiffStatus.Added    => ("added",    "green"),
                    DiffStatus.Deleted  => ("deleted",  "red"),
                    DiffStatus.Modified => ("modified", "yellow"),
                    _                   => ("?",        "dim"),
                };
                table.AddRow($"[{color}]{label}[/]", e.Path);
            }

            AnsiConsole.MarkupLine(
                $"[bold]diff[/] [yellow]{s.From}[/] → [yellow]{s.To}[/]");
            AnsiConsole.Write(table);

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex)   { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs snap checkout ─────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs snap checkout</c> command.
/// </summary>
public sealed class SnapCheckoutSettings : SnapBaseSettings
{
    /// <summary>
    /// Gets the handle of the snapshot to restore the working tree to (e.g. <c>T0002</c>).
    /// </summary>
    [CommandArgument(0, "<handle>")]
    [Description("Snap handle to restore the working tree to (e.g. T0002).")]
    public string Handle { get; init; } = "";
}

/// <summary>
/// Implements the <c>dxs snap checkout</c> command, which restores the workspace working
/// tree to the state recorded in the specified snapshot and records the result as a new
/// snapshot in the session.
/// </summary>
/// <remarks>
/// <para>
/// Checkout is implemented as a <see cref="RollbackEngine"/> restore followed by a new
/// snapshot of the resulting tree. If the restored tree is identical to an existing
/// snapshot (e.g. checking out the current HEAD), the existing handle is reused and no
/// new snapshot is created.
/// </para>
/// <para>
/// This operation acquires the workspace lock for its duration. Concurrent <c>dxs</c>
/// operations against the same workspace will be blocked until checkout completes.
/// </para>
/// </remarks>
public sealed class SnapCheckoutCommand : DxCommandBase<SnapCheckoutSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext ctx, SnapCheckoutSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);

            string newHandle = string.Empty;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Checking out {s.Handle}...", async _ =>
                {
                    await runtime.CheckoutAsync(s.Handle).ContinueWith(t =>
                    {
                        if (t.IsFaulted) throw t.Exception!;
                        newHandle = t.Result;
                    });
                });
        
            AnsiConsole.MarkupLine(
                $"[green]Checked out[/] [yellow]{s.Handle}[/] → [yellow]{newHandle}[/]");

            return 0;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex)   { return HandleUnexpected(ex); }
    }
}
