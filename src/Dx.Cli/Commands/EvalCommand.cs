using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Dx.Cli.Commands;

// ── dx eval ───────────────────────────────────────────────────────────────────

public sealed class EvalSettings : CommandSettings
{
    [CommandArgument(0, "<snap-a>")]
    [Description("Baseline snap handle (e.g. T0000).")]
    public string SnapA { get; init; } = "";

    [CommandArgument(1, "<snap-b>")]
    [Description("Candidate snap handle (e.g. T0005).")]
    public string SnapB { get; init; } = "";

    [CommandArgument(2, "<command>")]
    [Description("Command to run against both snaps.")]
    public string Command { get; init; } = "";

    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--session <id>")]
    public string? Session { get; init; }

    [CommandOption("--timeout <seconds>")]
    [DefaultValue(0)]
    public int Timeout { get; init; }

    [CommandOption("--sequential")]
    [Description("Run serially instead of in parallel.")]
    public bool Sequential { get; init; }

    [CommandOption("--pass-if <expr>")]
    [Description("Pass condition: exit-equal | both-pass | b-passes | no-regression. Default: b-passes")]
    [DefaultValue("b-passes")]
    public string PassIf { get; init; } = "b-passes";

    [CommandOption("--label-a <label>")]
    [Description("Label for snap-a in output. Default: snap handle.")]
    public string? LabelA { get; init; }

    [CommandOption("--label-b <label>")]
    [Description("Label for snap-b in output. Default: snap handle.")]
    public string? LabelB { get; init; }

    [CommandOption("--artifacts-dir <path>")]
    [Description("Directory for run outputs (not snapped).")]
    public string? ArtifactsDir { get; init; }
}

public sealed class EvalCommand : DxCommandBase<EvalSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, EvalSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);

            var labelA = s.LabelA ?? s.SnapA;
            var labelB = s.LabelB ?? s.SnapB;

            // Materialize both snaps into isolated temp dirs
            string dirA, dirB;

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

            // Suppress uninitialized warning — Status() always runs the action
            dirA = await runtime.MaterializeSnapAsync(s.SnapA);
            dirB = await runtime.MaterializeSnapAsync(s.SnapB);

            AnsiConsole.MarkupLine($"[bold]eval:[/] [dim]{s.Command}[/]");
            AnsiConsole.WriteLine();

            // Run command against both snaps
            (int ExitCode, string Output, long Ms) resultA, resultB;

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

            // Re-run to get actual results (workaround for lambda capture)
            resultA = await RunTimed(s.Command, dirA, s.Timeout);
            resultB = await RunTimed(s.Command, dirB, s.Timeout);

            // Clean up temp dirs
            foreach (var dir in new[] { dirA, dirB })
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }

            // Render results table
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

            // Evaluate pass condition
            var passed = s.PassIf switch
            {
                "exit-equal" => resultA.ExitCode == resultB.ExitCode,
                "both-pass" => resultA.ExitCode == 0 && resultB.ExitCode == 0,
                "b-passes" => resultB.ExitCode == 0,
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
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    private static async Task<(int ExitCode, string Output, long Ms)> RunTimed(
        string command, string workDir, int timeout)
    {
        var sw = Stopwatch.StartNew();
        var (exit, output) = await RunCommand.ExecuteAsync(command, workDir, timeout);
        sw.Stop();
        return (exit, output, sw.ElapsedMilliseconds);
    }

    private static string ExitMarkup(int code)
        => code == 0 ? "[green]0[/]" : $"[red]{code}[/]";

    private static string FirstLine(string output)
    {
        var line = output.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        return line.Length > 60 ? line[..57] + "..." : line;
    }
}
