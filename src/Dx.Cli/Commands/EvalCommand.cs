using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Dx.Cli.Commands;

// ── dxs eval ─────────────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs eval</c> command, which materialises two
/// snapshots into isolated directories and compares the result of running the same
/// command against each.
/// </summary>
public sealed class EvalSettings : CommandSettings
{
    /// <summary>
    /// Gets the handle of the baseline snapshot (e.g. <c>T0000</c>).
    /// The baseline is used as the reference point for pass-condition evaluation.
    /// </summary>
    [CommandArgument(0, "<snap-a>")]
    [Description("Baseline snap handle (e.g. T0000).")]
    public string SnapA { get; init; } = "";

    /// <summary>
    /// Gets the handle of the candidate snapshot (e.g. <c>T0005</c>).
    /// The candidate is the version under test.
    /// </summary>
    [CommandArgument(1, "<snap-b>")]
    [Description("Candidate snap handle (e.g. T0005).")]
    public string SnapB { get; init; } = "";

    /// <summary>
    /// Gets the shell command to execute against both snapshots.
    /// The command is run via the system shell (<c>cmd.exe /c</c> on Windows, <c>/bin/sh -c</c> elsewhere).
    /// </summary>
    [CommandArgument(2, "<command>")]
    [Description("Shell command to run against both snaps.")]
    public string Command { get; init; } = "";

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

    /// <summary>
    /// Gets the per-snapshot execution timeout in seconds.
    /// A value of <c>0</c> (the default) means no timeout is applied.
    /// </summary>
    [CommandOption("-t|--timeout <seconds>")]
    [Description("Per-snapshot command timeout in seconds. 0 = no timeout.")]
    [DefaultValue(0)]
    public int Timeout { get; init; }

    /// <summary>
    /// Gets a value indicating whether the two snapshots should be materialised and
    /// executed sequentially rather than concurrently.
    /// Use this flag when the command itself requires exclusive filesystem or port access.
    /// </summary>
    [CommandOption("--sequential")]
    [Description("Materialise and execute snaps one after the other instead of concurrently.")]
    public bool Sequential { get; init; }

    /// <summary>
    /// Gets the pass-condition expression that determines the overall eval outcome.
    /// </summary>
    /// <value>
    /// One of:
    /// <list type="bullet">
    ///   <item><description><c>b-passes</c> — candidate exit code is <c>0</c> (default).</description></item>
    ///   <item><description><c>exit-equal</c> — both exit codes are identical.</description></item>
    ///   <item><description><c>both-pass</c> — both exit codes are <c>0</c>.</description></item>
    ///   <item><description><c>no-regression</c> — candidate exit code ≤ baseline exit code.</description></item>
    /// </list>
    /// </value>
    [CommandOption("-p|--pass-if <expr>")]
    [Description("Pass condition: exit-equal | both-pass | b-passes | no-regression.")]
    [DefaultValue("b-passes")]
    public string PassIf { get; init; } = "b-passes";

    /// <summary>
    /// Gets an optional display label for the baseline snapshot in the results table.
    /// Defaults to the raw snap handle when not specified.
    /// </summary>
    [CommandOption("--label-a <label>")]
    [Description("Display label for snap-a in the results table. Defaults to the snap handle.")]
    public string? LabelA { get; init; }

    /// <summary>
    /// Gets an optional display label for the candidate snapshot in the results table.
    /// Defaults to the raw snap handle when not specified.
    /// </summary>
    [CommandOption("--label-b <label>")]
    [Description("Display label for snap-b in the results table. Defaults to the snap handle.")]
    public string? LabelB { get; init; }

    /// <summary>
    /// Gets the directory into which command output artifacts may be written.
    /// Files placed here are not captured in any snapshot.
    /// </summary>
    [CommandOption("--artifacts-dir <path>")]
    [Description("Directory for command output artifacts (excluded from snaps).")]
    public string? ArtifactsDir { get; init; }
}

/// <summary>
/// Implements the <c>dxs eval</c> command, which materialises two snapshots into isolated
/// temporary directories, executes a shell command against each, and evaluates the results
/// according to the specified pass condition.
/// </summary>
/// <remarks>
/// <para>
/// Both snapshots are materialised (and optionally executed) concurrently by default to
/// minimise wall-clock time. Pass <c>--sequential</c> to serialise the runs.
/// </para>
/// <para>
/// The temporary directories created during materialisation are deleted after the command
/// completes, regardless of outcome.
/// </para>
/// <para>
/// Exit codes: <c>0</c> when the pass condition is satisfied, <c>1</c> when it is not or
/// when a <see cref="DxException"/> is thrown, <c>2</c> for an invalid argument.
/// </para>
/// </remarks>
public sealed class EvalCommand : DxCommandBase<EvalSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext ctx, EvalSettings s)
    {
        try
        {
            var root    = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);

            var labelA = s.LabelA ?? s.SnapA;
            var labelB = s.LabelB ?? s.SnapB;

            // ── Materialise both snaps (exactly once) ─────────────────────────
            string dirA = string.Empty, dirB = string.Empty;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Preparing snap states...", async _ =>
                {
                    if (s.Sequential)
                    {
                        dirA = await runtime.MaterializeSnapAsync(s.SnapA);
                        dirB = await runtime.MaterializeSnapAsync(s.SnapB);
                    }
                    else
                    {
                        var taskA = runtime.MaterializeSnapAsync(s.SnapA);
                        var taskB = runtime.MaterializeSnapAsync(s.SnapB);
                        await Task.WhenAll(taskA, taskB);
                        dirA = taskA.Result;
                        dirB = taskB.Result;
                    }
                });

            AnsiConsole.MarkupLine($"[bold]eval:[/] [dim]{s.Command}[/]");
            AnsiConsole.WriteLine();

            // ── Run command against both snaps (exactly once) ─────────────────
            (int ExitCode, string Output, long Ms) resultA = default,
                                                   resultB = default;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Running against {labelA} and {labelB}...", async _ =>
                {
                    if (s.Sequential)
                    {
                        resultA = await RunTimed(s.Command, dirA, s.Timeout);
                        resultB = await RunTimed(s.Command, dirB, s.Timeout);
                    }
                    else
                    {
                        var taskA = RunTimed(s.Command, dirA, s.Timeout);
                        var taskB = RunTimed(s.Command, dirB, s.Timeout);
                        await Task.WhenAll(taskA, taskB);
                        resultA = taskA.Result;
                        resultB = taskB.Result;
                    }
                });

            // ── Clean up temp dirs ────────────────────────────────────────────
            foreach (var dir in new[] { dirA, dirB })
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }

            // ── Render results table ──────────────────────────────────────────
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Snap")
                .AddColumn(new TableColumn("Exit").Centered())
                .AddColumn(new TableColumn("Duration").RightAligned())
                .AddColumn("Output (first line)");

            table.AddRow(
                $"[dim]baseline[/] [yellow]{labelA}[/]",
                ExitMarkup(resultA.ExitCode),
                $"{resultA.Ms}ms",
                FirstLine(resultA.Output));

            table.AddRow(
                $"[dim]candidate[/] [yellow]{labelB}[/]",
                ExitMarkup(resultB.ExitCode),
                $"{resultB.Ms}ms",
                FirstLine(resultB.Output));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // ── Evaluate pass condition ───────────────────────────────────────
            var passed = s.PassIf switch
            {
                "exit-equal"    => resultA.ExitCode == resultB.ExitCode,
                "both-pass"     => resultA.ExitCode == 0 && resultB.ExitCode == 0,
                "b-passes"      => resultB.ExitCode == 0,
                "no-regression" => resultB.ExitCode <= resultA.ExitCode,
                _ => throw new DxException(DxError.InvalidArgument,
                    $"Unknown pass-if expression: {s.PassIf}. " +
                    "Valid values: exit-equal, both-pass, b-passes, no-regression")
            };

            if (passed)
                AnsiConsole.MarkupLine($"[green]PASS[/] [dim]({s.PassIf})[/]");
            else
                AnsiConsole.MarkupLine($"[red]FAIL[/] [dim]({s.PassIf})[/]");

            return passed ? 0 : 1;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex)   { return HandleUnexpected(ex); }
    }

    /// <summary>
    /// Executes a shell command in the specified working directory and measures elapsed time.
    /// </summary>
    /// <param name="command">The shell command string to execute.</param>
    /// <param name="workDir">The working directory in which to run the command.</param>
    /// <param name="timeout">Timeout in seconds; <c>0</c> means no timeout.</param>
    /// <returns>
    /// A tuple containing the process exit code, the combined stdout/stderr output,
    /// and the elapsed wall-clock time in milliseconds.
    /// </returns>
    private static async Task<(int ExitCode, string Output, long Ms)> RunTimed(
        string command, string workDir, int timeout)
    {
        var sw = Stopwatch.StartNew();
        var (exit, output) = await RunCommand.ExecuteAsync(command, workDir, timeout);
        sw.Stop();
        return (exit, output, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Returns a Spectre Console markup string representing the given exit code,
    /// rendered green for success (<c>0</c>) and red for any non-zero value.
    /// </summary>
    /// <param name="code">The process exit code to format.</param>
    /// <returns>A markup string suitable for use in an <see cref="AnsiConsole"/> table cell.</returns>
    private static string ExitMarkup(int code)
        => code == 0 ? "[green]0[/]" : $"[red]{code}[/]";

    /// <summary>
    /// Extracts and truncates the first non-blank line from a command's combined output,
    /// suitable for display in a summary table column.
    /// </summary>
    /// <param name="output">The raw combined stdout/stderr string.</param>
    /// <returns>The first non-blank line, truncated to 60 characters with an ellipsis if longer.</returns>
private static string FirstLine(string? output)
    {
        var line = output?.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        return line.Length > 60 ? line[..57] + "..." : line;
    }
}

