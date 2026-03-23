using Dx.Core;
using Dx.Core.Protocol;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

/// <summary>
/// Defines the settings for the <c>dxs apply</c> command, specifying the target
/// document, workspace root, and execution options.
/// </summary>
public sealed class ApplySettings : CommandSettings
{
    /// <summary>
    /// Gets the path to the <c>.dx</c> document to apply.
    /// Pass <c>-</c> (or omit entirely) to read the document from standard input.
    /// </summary>
    [CommandArgument(0, "[file]")]
    [Description("Path to a .dx document, or '-' to read from stdin. Defaults to stdin.")]
    [DefaultValue("-")]
    public string File { get; init; } = "-";

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
    /// Gets a value indicating whether to perform a dry run.
    /// In dry-run mode the document is parsed and validated but no changes are
    /// written and no snapshot is created.
    /// </summary>
    [CommandOption("-n|--dry-run")]
    [Description("Parse and validate the document without applying any changes.")]
    public bool DryRun { get; init; }

    /// <summary>
    /// Gets a value indicating whether verbose diagnostic output is enabled.
    /// Verbose output is written to stderr so it does not interfere with piped stdout.
    /// </summary>
    [CommandOption("-v|--verbose")]
    [Description("Emit detailed diagnostic output to stderr.")]
    public bool Verbose { get; init; }

    /// <summary>
    /// Gets the per-invocation base-mismatch behaviour override.
    /// When <see langword="null"/> the value from the workspace configuration
    /// (<c>conflict.on_base_mismatch</c>) is used.
    /// </summary>
    /// <value>
    /// <c>reject</c> — abort the transaction on mismatch (exit code 3).<br/>
    /// <c>warn</c>   — emit a warning and continue applying the document.
    /// </value>
    [CommandOption("--on-base-mismatch <reject|warn>")]
    [Description("Override base-mismatch behaviour for this invocation: reject (default) or warn.")]
    public string? OnBaseMismatch { get; init; }

    /// <summary>
    /// Gets the per-invocation timeout in seconds for <c>%%REQUEST type=\"run\"</c>
    /// gate blocks. When <c>0</c> or <see langword="null"/>, no timeout is applied.
    /// Overrides the workspace configuration value (<c>run.run_timeout</c>) for this
    /// invocation only.
    /// </summary>
    [CommandOption("--run-timeout <seconds>")]
    [Description("Timeout in seconds for run gate blocks. 0 = no timeout. Overrides config for this invocation.")]
    [DefaultValue(0)]
    public int RunTimeout { get; init; }
}

/// <summary>
/// Implements the <c>dxs apply</c> command, which parses a DX document and executes
/// it as an atomic transaction against the workspace.
/// </summary>
/// <remarks>
/// <para>
/// The command reads either a file path or stdin, passes the text through
/// <see cref="DxParser"/>, then hands the resulting <see cref="DxDocument"/> to
/// <see cref="DxRuntime.ApplyAsync"/>.
/// </para>
/// <para>
/// On success a new snapshot handle (e.g. <c>T0003</c>) is printed to stdout and the
/// process exits with code <c>0</c>. On parse failure the exit code is <c>2</c>. On
/// transaction failure the working tree is rolled back and the exit code is <c>1</c>.
/// On base mismatch the exit code is <c>3</c>.
/// </para>
/// </remarks>
public sealed class ApplyCommand : DxCommandBase<ApplySettings>
{
    /// <inheritdoc />
    /// <exception cref="DxException">
    /// Thrown when a DX-specific error occurs during execution.
    /// </exception>
    public override async Task<int> ExecuteAsync(CommandContext ctx, ApplySettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Session, new ConsoleDxLogger(s.Verbose));

            // Read document
            string text;
            if (s.File == "-")
                text = await Console.In.ReadToEndAsync();
            else
                text = await System.IO.File.ReadAllTextAsync(s.File);

            // Parse
            var (doc, errors) = DxParser.ParseText(text);

            if (errors.Count > 0)
            {
                foreach (var e in errors)
                    Console.Error.WriteLine($"parse error line {e.Line}: {e.Message}");
                return 2;
            }

            if (doc is null)
            {
                Console.Error.WriteLine("error: Failed to parse document.");
                return 2;
            }

            if (s.DryRun)
            {
                AnsiConsole.MarkupLine("[yellow]dry run[/] — no changes applied.");
                RenderDocumentSummary(doc);
                return 0;
            }

            // Build apply options from per-invocation flags
            var applyOptions = new ApplyOptions(
                OnBaseMismatch: s.OnBaseMismatch,
                RunTimeoutSeconds: s.RunTimeout > 0 ? s.RunTimeout : null);

            // Apply with progress
            DispatchResult result = null!;

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx2 =>
                {
                    var task = ctx2.AddTask("Applying...", maxValue: 100);
                    var progress = new Progress<string>(msg =>
                    {
                        task.Description = msg;
                        task.Increment(10);
                    });

                    result = await runtime.ApplyAsync(doc, dryRun: false, progress,
                        options: applyOptions);
                });

            RenderApplyResult(result);

            return result.Success ? 0 : (result.IsBaseMismatch ? 3 : 1);
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }

    /// <summary>
    /// Renders a formatted table summarising each block operation and the resulting
    /// snapshot handle, or an error message with rollback confirmation if the
    /// transaction failed.
    /// </summary>
    /// <remarks>
    /// Success output (table + handle) goes to stdout.
    /// Failure output goes to stderr so it does not pollute piped stdout.
    /// </remarks>
    private static void RenderApplyResult(DispatchResult result)
    {
        if (!result.Success)
        {
            Console.Error.WriteLine($"error: {result.Error}");
            Console.Error.WriteLine("Working tree rolled back.");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Block")
            .AddColumn("Path")
            .AddColumn("Result");

        foreach (var op in result.Operations)
        {
            var status = op.Success ? "[green]ok[/]" : "[red]failed[/]";
            table.AddRow(
                op.BlockType,
                op.Path ?? "[dim]—[/]",
                op.Detail is not null ? $"{status} {op.Detail}" : status);
        }

        if (result.NewHandle is not null)

            AnsiConsole.MarkupLine($"[green]→[/] [yellow]{result.NewHandle}[/]");

        else

            AnsiConsole.MarkupLine("[dim]No changes (no-op).[/]");

    }
    /// Renders a concise summary of the parsed document's header fields and block
    /// count. Used in dry-run mode to show what would have been applied.
    /// </summary>
    private static void RenderDocumentSummary(DxDocument doc)
    {
        AnsiConsole.MarkupLine($"  Session: [cyan]{doc.Header.Session ?? "(none)"}[/]");
        AnsiConsole.MarkupLine($"  Base:    [yellow]{doc.Header.Base ?? "(none)"}[/]");
        AnsiConsole.MarkupLine($"  Blocks:  {doc.Blocks.Count}");
        AnsiConsole.MarkupLine($"  Mutating: {doc.IsMutating}");
    }
}


