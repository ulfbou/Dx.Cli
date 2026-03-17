// file: src/Dx.Cli/Program.cs
using Dx.Cli.Commands;

using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("dx");
    config.SetApplicationVersion("0.1.0");
    config.UseStrictParsing();

    // This ensures that when a validation error occurs (like missing arguments),
    // Spectre shows the help text instead of a stack trace.
    config.PropagateExceptions();

    config.AddCommand<InitCommand>("init")
          .WithDescription("Initialize a DX workspace and take genesis snap T0000.");

    config.AddCommand<ApplyCommand>("apply")
          .WithDescription("Apply a DX document to the working tree.");

    config.AddBranch("snap", snap =>
    {
        snap.SetDescription("Manage snapshots.");
        snap.AddCommand<SnapListCommand>("list")
            .WithDescription("List snaps in the current session.");
        snap.AddCommand<SnapShowCommand>("show")
            .WithDescription("Show details for a specific snap.");
        snap.AddCommand<SnapDiffCommand>("diff")
            .WithDescription("Diff two snaps.");
        snap.AddCommand<SnapCheckoutCommand>("checkout")
            .WithDescription("Restore working tree to a snap state.");
    });

    config.AddCommand<LogCommand>("log")
          .WithDescription("Show session log.");

    config.AddCommand<PackCommand>("pack")
          .WithDescription("Bundle files into a read-only DX document for LLM context.");

#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
});

try
{
    return app.Run(args);
}
catch (CommandParseException ex)
{
    return DxCliErrorRenderer.RenderParseError(ex);
}
catch (CommandRuntimeException ex)
{
    return DxCliErrorRenderer.RenderRuntimeError(ex);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
    return 1;
}

/// <summary>
/// Centralized DX-first CLI error rendering.
/// Keeps strict argument contracts while replacing Spectre-native errors
/// with domain-specific guidance.
/// </summary>
internal static class DxCliErrorRenderer
{
    public static int RenderParseError(CommandParseException ex)
    {
        var message = ex.Message.ToLowerInvariant();

        // Missing required argument: handle
        if (message.Contains("missing required argument") &&
            message.Contains("handle"))
        {
            AnsiConsole.MarkupLine("[red]Missing required argument: handle[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine(
                "[yellow]A snap handle identifies a snapshot (e.g. T0000)[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[blue]Discover available snaps:[/]");
            AnsiConsole.MarkupLine("  dx snap list");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[blue]Show a snap:[/]");
            AnsiConsole.MarkupLine("  dx snap show T0000");

            return 1;
        }

        // Unknown command
        if (message.Contains("unknown command"))
        {
            AnsiConsole.MarkupLine("[red]Unknown command[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[yellow]Available commands:[/]");
            AnsiConsole.MarkupLine("  dx init");
            AnsiConsole.MarkupLine("  dx apply <file>");
            AnsiConsole.MarkupLine("  dx snap list");
            AnsiConsole.MarkupLine("  dx snap show <handle>");
            AnsiConsole.MarkupLine("  dx snap diff <a> <b>");
            AnsiConsole.MarkupLine("  dx snap checkout <handle>");
            AnsiConsole.MarkupLine("  dx log");
            AnsiConsole.MarkupLine("  dx pack");

            return 1;
        }

        // Fallback: preserve original message
        AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
        return 1;
    }

    public static int RenderRuntimeError(CommandRuntimeException ex)
    {
        var message = ex.Message;

        // Snap not found (string match keeps compatibility with existing code)
        if (message.Contains("Snap not found", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]{message}[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[blue]List available snaps:[/]");
            AnsiConsole.MarkupLine("  dx snap list");

            return 1;
        }

        // Fallback
        AnsiConsole.MarkupLine($"[red]{message}[/]");
        return 1;
    }
}
