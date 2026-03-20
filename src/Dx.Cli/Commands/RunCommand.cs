using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Diagnostics;

namespace Dx.Cli.Commands;

// ── dxs run ───────────────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs run</c> command, which executes a shell command
/// against the current working tree or an isolated materialised snapshot.
/// </summary>
public sealed class RunSettings : CommandSettings
{
    /// <summary>
    /// Gets the shell command string to execute.
    /// Use <c>--</c> to unambiguously separate <c>dxs</c> options from command tokens
    /// that begin with a dash.
    /// </summary>
    [CommandArgument(0, "<command>")]
    [Description("Shell command to execute. Use -- to separate dxs options from the command.")]
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
    /// Gets the handle of the snapshot to run against.
    /// When specified, the snapshot is materialised into an isolated temporary directory
    /// and the command executes there, leaving the working tree untouched.
    /// When omitted, the command runs directly against the current working tree at HEAD.
    /// </summary>
    [CommandOption("--snap <handle>")]
    [Description("Run against this snap state in isolation (working tree unchanged). Defaults to HEAD.")]
    public string? Snap { get; init; }

    /// <summary>
    /// Gets the command execution timeout in seconds.
    /// A value of <c>0</c> (the default) means no timeout is applied and the command
    /// runs until it completes naturally.
    /// </summary>
    [CommandOption("-t|--timeout <seconds>")]
    [Description("Command timeout in seconds. 0 = no timeout.")]
    [DefaultValue(0)]
    public int Timeout { get; init; }

    /// <summary>
    /// Gets the path to a directory for command output artifacts that should not be
    /// captured in any snapshot. Useful for build outputs or test reports.
    /// </summary>
    [CommandOption("-a|--artifacts-dir <path>")]
    [Description("Directory for command output artifacts (excluded from snaps).")]
    public string? ArtifactsDir { get; init; }
}

/// <summary>
/// Implements the <c>dxs run</c> command, which executes a shell command either directly
/// against the working tree or against a materialised snapshot in an isolated directory.
/// </summary>
/// <remarks>
/// <para>
/// When <c>--snap</c> is provided the snapshot is materialised into a temporary directory,
/// the command runs there, and the directory is cleaned up on exit regardless of outcome.
/// The working tree is never modified by this command.
/// </para>
/// <para>
/// The combined stdout and stderr of the child process are forwarded to the caller's
/// stdout. The command's exit code becomes the exit code of <c>dxs run</c>.
/// </para>
/// </remarks>
public sealed class RunCommand : DxCommandBase<RunSettings>
{
    /// <summary>
    /// Executes the run command, optionally materialising a snapshot, then executing
    /// the requested shell command and cleaning up any temporary state.
    /// </summary>
    /// <param name="ctx">The Spectre.Console command context.</param>
    /// <param name="s">The parsed run settings.</param>
    /// <returns>
    /// A task that resolves to the exit code of the child process, or a <c>dxs</c>
    /// error code if workspace resolution fails.
    /// </returns>
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

    /// <summary>
    /// Low-level helper that spawns a shell process, waits for it to complete (or times out),
    /// and captures the combined stdout and stderr output.
    /// </summary>
    /// <param name="command">The shell command string to execute.</param>
    /// <param name="workDir">The working directory for the spawned process.</param>
    /// <param name="timeoutSeconds">
    /// Maximum execution time in seconds. Pass <c>0</c> for no timeout.
    /// When the timeout expires the process tree is killed and exit code <c>124</c> is returned.
    /// </param>
    /// <returns>
    /// A task that resolves to a tuple containing the process exit code and the combined
    /// stdout/stderr output as a single string.
    /// </returns>
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
