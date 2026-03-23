using Dx.Cli.Commands;

using Spectre.Console;
using Spectre.Console.Cli;

// ── Global flags handled before app.Run ──────────────────────────────────────

// --no-color: disable ANSI colour output for script-friendly use.
// Must be applied before AnsiConsole is first used.
if (args.Contains("--no-color"))
{
    AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
    AnsiConsole.Profile.Capabilities.Ansi = false;
    args = args.Where(a => a != "--no-color").ToArray();
}

// vNext state model scaffold — not yet implemented.
// Set DX_VNEXT_STATE_MODEL=1 to opt in when available.
if (Environment.GetEnvironmentVariable("DX_VNEXT_STATE_MODEL") == "1")
    Console.Error.WriteLine(
        "warn: DX_VNEXT_STATE_MODEL is set but vNext is not yet active in this build.");

// ── Application setup ─────────────────────────────────────────────────────────

// ── Pipe safety ───────────────────────────────────────────────────────────────

// When stdout is redirected (piped or written to a file), route all AnsiConsole

// output to stderr so that structured data written to Console.Out is never

// corrupted by progress bars, tables, spinners, or status messages.

//

// Interactive mode (stdout is a TTY) is unaffected: colours, progress bars,

// and the spinner continue to render on stdout as before.

//

// Commands that intentionally write data to stdout (dxs pack without --out,

// and the new snapshot handle from dxs apply) use Console.Write/WriteLine

// directly and are not affected by this rerouting.

if (Console.IsOutputRedirected)

    AnsiConsole.Profile.Out = new AnsiConsoleOutput(Console.Error);



var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("dxs");
    config.SetApplicationVersion("0.2.0");
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

        config.AddCommand<DoctorCommand>("doctor")
              .WithDescription("Inspect and optionally repair workspace health issues.");
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
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ── Error renderer ────────────────────────────────────────────────────────────

/// <summary>
/// Provides centralised, DX-first rendering of Spectre.Console CLI parse and runtime
/// errors. All output goes to stderr so it never contaminates piped stdout.
/// </summary>
/// <remarks>
/// Strict argument parsing is enabled globally so that typos and missing required
/// arguments surface immediately. This class replaces the generic Spectre error output
/// with domain-aware guidance that directs the user to the correct <c>dxs</c> commands
/// for recovery.
/// </remarks>
internal static class DxCliErrorRenderer
{
    /// <summary>
    /// Renders a user-facing error message for a <see cref="CommandParseException"/>
    /// and returns the appropriate process exit code.
    /// </summary>
    /// <remarks>
    /// All output goes to stderr. User-supplied text from the exception message is
    /// written with <see cref="Console.Error"/> rather than
    /// <see cref="AnsiConsole.MarkupLine"/> to avoid Spectre markup parse crashes
    /// when the message contains characters such as <c>--snap</c> or angle brackets
    /// that Spectre interprets as markup tokens.
    /// </remarks>
    /// <param name="ex">The parse exception thrown by Spectre.Console.</param>
    /// <returns>Always returns <c>2</c> (tool/parse error).</returns>
    public static int RenderParseError(CommandParseException ex)
    {
        var message = ex.Message.ToLowerInvariant();

        if (message.Contains("missing required argument") &&
            message.Contains("handle"))
        {
            Console.Error.WriteLine("error: Missing required argument: handle");
            Console.Error.WriteLine();
            Console.Error.WriteLine("A snap handle identifies a snapshot (e.g. T0000)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Discover available snaps:");
            Console.Error.WriteLine("  dxs snap list");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Show a snap:");
            Console.Error.WriteLine("  dxs snap show T0000");
            return 2;
        }

        if (message.Contains("unknown command"))
        {
            Console.Error.WriteLine("error: Unknown command");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Available commands:");
            Console.Error.WriteLine("  dxs init [path]");
            Console.Error.WriteLine("  dxs apply [file]");
            Console.Error.WriteLine("  dxs snap list|show|diff|checkout");
            Console.Error.WriteLine("  dxs session list|new|show|close");
            Console.Error.WriteLine("  dxs log");
            Console.Error.WriteLine("  dxs pack <path>");
            Console.Error.WriteLine("  dxs run [--snap <handle>] <command>");
            Console.Error.WriteLine("  dxs eval <snap-a> <snap-b> <command>");
            Console.Error.WriteLine("  dxs config get|set|unset|list|show-effective");
            return 2;
        }

        // Fall back: write the raw message to stderr without markup parsing
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    /// <summary>
    /// Renders a user-facing error message for a <see cref="CommandRuntimeException"/>
    /// and returns the appropriate process exit code.
    /// </summary>
    /// <remarks>
    /// All output goes to stderr. User-supplied text is written with
    /// <see cref="Console.Error"/> to avoid Spectre markup parse crashes.
    /// </remarks>
    /// <param name="ex">The runtime exception thrown by Spectre.Console.</param>
    /// <returns>Always returns <c>1</c>.</returns>
    public static int RenderRuntimeError(CommandRuntimeException ex)
    {
        var message = ex.Message;

        Console.Error.WriteLine($"error: {message}");

        if (message.Contains("Snap not found", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("List available snaps:");
            Console.Error.WriteLine("  dxs snap list");
        }

        return 1;
    }
}

