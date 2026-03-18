using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Diagnostics;

namespace Dx.Cli.Commands;

// ── dx run ────────────────────────────────────────────────────────────────────

public sealed class RunSettings : CommandSettings
{
    [CommandArgument(0, "<command>")]
    [Description("Command to execute. Use -- to separate from dx flags.")]
    public string Command { get; init; } = "";

    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--session <id>")]
    public string? Session { get; init; }

    [CommandOption("--snap <handle>")]
    [Description("Run against this snap state (read-only, working tree unchanged). Defaults to HEAD.")]
    public string? Snap { get; init; }

    [CommandOption("--timeout <seconds>")]
    [Description("Command timeout in seconds. 0 = no timeout.")]
    [DefaultValue(0)]
    public int Timeout { get; init; }

    [CommandOption("--artifacts-dir <path>")]
    [Description("Directory for command outputs (not snapped).")]
    public string? ArtifactsDir { get; init; }
}

public sealed class RunCommand : DxCommandBase<RunSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext ctx, RunSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session);

            string? snapHandle = s.Snap;
            string workDir;

            if (snapHandle is not null)
            {
                // Materialize snap in a temp directory; leave working tree untouched
                workDir = await runtime.MaterializeSnapAsync(snapHandle);
                AnsiConsole.MarkupLine(
                    $"[dim]Running against snap [yellow]{snapHandle}[/] (isolated)[/]");
            }
            else
            {
                workDir = root;
                snapHandle = runtime.GetHead() ?? "HEAD";
                AnsiConsole.MarkupLine(
                    $"[dim]Running against [yellow]{snapHandle}[/][/]");
            }

            AnsiConsole.MarkupLine($"[dim]{s.Command}[/]");
            AnsiConsole.WriteLine();

            var (exitCode, output) = await ExecuteAsync(s.Command, workDir, s.Timeout);

            Console.Write(output);

            if (s.Snap is not null)
            {
                // Clean up temp directory
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }

            return exitCode;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    internal static async Task<(int ExitCode, string Output)> ExecuteAsync(
        string command,
        string workDir,
        int timeoutSeconds = 0)
    {
        var (shell, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c {command}")
            : ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;

        using var cts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            return (124, $"Command timed out after {timeoutSeconds}s\n");
        }

        return (proc.ExitCode, (await stdout) + (await stderr));
    }
}
