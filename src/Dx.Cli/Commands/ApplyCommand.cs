using Dx.Cli.Infrastructure;
using Dx.Core;
using Dx.Core.Execution.Results;
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
    public string? WorkspaceRoot { get; init; }

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
    [CommandOption("--timeout <seconds>")]
    [Description("Timeout in seconds for run gate blocks. 0 = no timeout. Overrides config for this invocation.")]
    [DefaultValue(0)]
    public int RunTimeout { get; init; }
}

/// <summary>
/// Implements the <c>dxs apply</c> command, which reads a DX document and dispatches
/// it as an atomic transaction through <see cref="DxDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// The command reads either a file path or stdin, then hands the raw text directly
/// to the dispatcher, which owns parsing, validation, and execution.
/// </para>
/// <para>
/// On success a new snapshot handle (e.g. <c>T0003</c>) is printed to stdout and the
/// process exits with code <c>0</c>. On transaction failure the working tree is rolled
/// back and the exit code is <c>1</c>. On base mismatch the exit code is <c>3</c>.
/// </para>
/// </remarks>
public sealed class ApplyCommand : DxCommandBase<ApplySettings>
{
    private readonly DxRuntime _runtime;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyCommand"/> class with the specified runtime.
    /// </summary>
    /// <param name="runtime">The DX runtime instance to use for command execution.</param>
    public ApplyCommand(DxRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <inheritdoc />
    /// <exception cref="DxException">
    /// Thrown when a DX-specific error occurs during execution.
    /// </exception>
    public override async Task<int> ExecuteAsync(
        CommandContext commandContext,
        ApplySettings settings)
    {
        var ct = CreateCancellationToken();

        // 1. Read raw text — no parsing here

        var rawText = await File.ReadAllTextAsync(settings.File, ct);

        // 2. Delegate to dispatcher via CliDispatcherContext (Apply moved to dispatcher)
        var root = FindRoot(settings.WorkspaceRoot);
        var ctx = await CliDispatcherContext.OpenAsync(root, settings.Session, null, ct);
        var mode = settings.DryRun ? DxExecutionMode.DryRun : DxExecutionMode.Apply;
        var result = await ctx.Dispatcher.DispatchAsync(
            rawText,
            "user",
            mode,
            progress: null,
            onBaseMismatch: settings.OnBaseMismatch,
            runTimeoutSeconds: settings.RunTimeout,
            ct: ct);

        // 3. Exit code mapping — result already logged by Dispatcher
        if (result.IsSuccess)
            return 0;

        // Map first diagnostic to stable exit code
        var code = result.Diagnostics.FirstOrDefault()?.Code switch
        {
            "BASE_MISMATCH" => DxError.BaseMismatch,
            "DX-PARSE-001" => DxError.ParseError,
            _ => DxError.InvalidArgument
        };

        return DxException.ExitCode(code);
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

    private static void RenderResult(DxResult result)
    {
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"error: {result.Message}");
            Console.Error.WriteLine("Working tree rolled back.");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Block")
            .AddColumn("Path")
            .AddColumn("Result");

        foreach (var op in result.Blocks)
        {
            var status = op.Success ? "[green]ok[/]" : "[red]failed[/]";
            table.AddRow(
                op.BlockType,
                op.Path ?? "[dim]—[/]",
                op.Detail is not null ? $"{status} {op.Detail}" : status);
        }

        AnsiConsole.Write(table);

        if (result.SnapId is not null)
            AnsiConsole.MarkupLine($"[green]→[/] [yellow]{result.SnapId}[/]");
        else
            AnsiConsole.MarkupLine("[dim]No changes (no-op).[/]");
    }
}
