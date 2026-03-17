using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

public sealed class InitSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Root path to initialize. Defaults to current directory.")]
    public string? Path { get; init; }

    [CommandOption("--session <id>")]
    [Description("Session identifier. Defaults to timestamp.")]
    public string? Session { get; init; }

    [CommandOption("--artifacts-dir <path>")]
    [Description("Directory excluded from snaps (CI artifacts).")]
    public string? ArtifactsDir { get; init; }

    [CommandOption("--exclude <paths>")]
    [Description("Comma-separated additional paths to exclude.")]
    public string? Exclude { get; init; }

    [CommandOption("--include-build-output")]
    [Description("Include bin/ and obj/ in snaps (excluded by default).")]
    public bool IncludeBuildOutput { get; init; }

    [CommandOption("--verbose")]
    public bool Verbose { get; init; }
}

public sealed class InitCommand : DxCommandBase<InitSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, InitSettings s)
    {
        try
        {
            var root = FindRoot(s.Path);
            var sessionId = s.Session ?? $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var excludes = s.Exclude?.Split(',', StringSplitOptions.RemoveEmptyEntries);

            var handle = DxRuntime.Init(
                root, sessionId, s.ArtifactsDir, excludes,
                s.IncludeBuildOutput,
                new ConsoleDxLogger(s.Verbose));

            AnsiConsole.MarkupLine($"[green]Initialized[/] DX workspace");
            AnsiConsole.MarkupLine($"  Root:    [dim]{root}[/]");
            AnsiConsole.MarkupLine($"  Session: [cyan]{sessionId}[/]");
            AnsiConsole.MarkupLine($"  Genesis: [yellow]{handle}[/]");

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}
