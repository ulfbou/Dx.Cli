using Dx.Cli.Commands;

using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("dx");
    config.SetApplicationVersion("0.1.0");
    config.UseStrictParsing();

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

    config.AddBranch("session", session =>
    {
        session.SetDescription("Manage sessions.");
        session.AddCommand<SessionListCommand>("list")
               .WithDescription("List all sessions in this workspace.");
        session.AddCommand<SessionNewCommand>("new")
               .WithDescription("Start a new session (new T0000 from current tree).");
        session.AddCommand<SessionShowCommand>("show")
               .WithDescription("Show session metadata and recent activity.");
        session.AddCommand<SessionCloseCommand>("close")
               .WithDescription("Close a session.");
    });

    config.AddCommand<LogCommand>("log")
          .WithDescription("Show session log.");

    config.AddCommand<PackCommand>("pack")
          .WithDescription("Bundle files into a read-only DX document for LLM context.");

    config.AddCommand<RunCommand>("run")
          .WithDescription("Execute a command against a specific snap state.");

    config.AddCommand<EvalCommand>("eval")
          .WithDescription("Compare two snaps by running the same command against each.");

    config.AddBranch("config", cfg =>
    {
        cfg.SetDescription("Read and write configuration.");
        cfg.AddCommand<ConfigGetCommand>("get")
           .WithDescription("Get a config value.");
        cfg.AddCommand<ConfigSetCommand>("set")
           .WithDescription("Set a config value.");
        cfg.AddCommand<ConfigUnsetCommand>("unset")
           .WithDescription("Remove a config value.");
        cfg.AddCommand<ConfigListCommand>("list")
           .WithDescription("List all config values at a scope.");
        cfg.AddCommand<ConfigShowEffectiveCommand>("show-effective")
           .WithDescription("Show the merged effective configuration.");
    });

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

        if (message.Contains("missing required argument") &&
            message.Contains("handle"))
        {
            AnsiConsole.MarkupLine("[red]Missing required argument: handle[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]A snap handle identifies a snapshot (e.g. T0000)[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Discover available snaps:[/]");
            AnsiConsole.MarkupLine("  dx snap list");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Show a snap:[/]");
            AnsiConsole.MarkupLine("  dx snap show T0000");
            return 1;
        }

        if (message.Contains("unknown command"))
        {
            AnsiConsole.MarkupLine("[red]Unknown command[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Available commands:[/]");
            AnsiConsole.MarkupLine("  dx init");
            AnsiConsole.MarkupLine("  dx apply <file>");
            AnsiConsole.MarkupLine("  dx snap list|show|diff|checkout");
            AnsiConsole.MarkupLine("  dx session list|new|show|close");
            AnsiConsole.MarkupLine("  dx log");
            AnsiConsole.MarkupLine("  dx pack <path>");
            AnsiConsole.MarkupLine("  dx run [--snap <handle>] -- <command>");
            AnsiConsole.MarkupLine("  dx eval <snap-a> <snap-b> -- <command>");
            AnsiConsole.MarkupLine("  dx config get|set|unset|list|show-effective");
            return 1;
        }

        AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
        return 1;
    }

    public static int RenderRuntimeError(CommandRuntimeException ex)
    {
        var message = ex.Message;

        if (message.Contains("Snap not found", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]{message}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]List available snaps:[/]");
            AnsiConsole.MarkupLine("  dx snap list");
            return 1;
        }

        AnsiConsole.MarkupLine($"[red]{message}[/]");
        return 1;
    }
}
