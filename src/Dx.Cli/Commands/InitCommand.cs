using Dx.Core;
using Dx.Core.Genesis;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

/// <summary>
/// Defines the settings for the <c>dxs init</c> command, which creates a new DX
/// workspace and takes the genesis snapshot <c>T0000</c>.
/// </summary>
public sealed class InitSettings : CommandSettings
{
    /// <summary>
    /// Gets the directory path to initialise as a DX workspace.
    /// Defaults to the current working directory when omitted.
    /// </summary>
    [CommandArgument(0, "[path]")]
    [Description("Directory to initialise as a DX workspace. Defaults to the current directory.")]
    public string? Path { get; init; }

    /// <summary>
    /// Gets the session identifier to assign to the genesis session.
    /// Defaults to a timestamp-based identifier (<c>session-yyyyMMdd-HHmmss</c>) when omitted.
    /// </summary>
    [CommandOption("-s|--session <id>")]
    [Description("Session identifier for the genesis session. Defaults to a UTC timestamp.")]
    public string? Session { get; init; }

    /// <summary>
    /// Gets the path to a directory whose contents should be excluded from all snapshots.
    /// This is typically a CI build artifact output directory.
    /// </summary>
    [CommandOption("-a|--artifacts-dir <path>")]
    [Description("Directory excluded from all snaps (e.g. a CI artifact output directory).")]
    public string? ArtifactsDir { get; init; }

    /// <summary>
    /// Gets a comma-separated list of additional relative or absolute paths to exclude
    /// from snapshots, supplementing the built-in exclusion list.
    /// </summary>
    [CommandOption("-x|--exclude <paths>")]
    [Description("Comma-separated additional paths to exclude from snaps.")]
    public string? Exclude { get; init; }

    /// <summary>
    /// Gets a value indicating whether build output directories (<c>bin/</c> and <c>obj/</c>)
    /// should be included in snapshots.
    /// By default, build outputs are excluded to keep snapshots lean.
    /// </summary>
    [CommandOption("-b|--include-build-output")]
    [Description("Include bin/ and obj/ directories in snaps. Excluded by default.")]
    public bool IncludeBuildOutput { get; init; }

    /// <summary>
    /// Gets a value indicating whether verbose diagnostic output is enabled.
    /// Verbose output is written to stderr and does not interfere with piped stdout.
    /// </summary>
    [CommandOption("-v|--verbose")]
    [Description("Emit detailed diagnostic output to stderr.")]
    public bool Verbose { get; init; }
}

/// <summary>
/// Implements the <c>dxs init</c> command, which initialises a new DX workspace at the
/// specified path and creates the genesis snapshot <c>T0000</c>.
/// </summary>
/// <remarks>
/// <para>
/// Initialisation creates a <c>.dx/</c> directory containing the workspace database
/// (<c>snap.db</c>), registers the genesis session, and takes an initial snapshot of
/// the current working tree.
/// </para>
/// <para>
/// As part of genesis, all file inclusion and exclusion semantics are fully resolved
/// and materialised into a deterministic <see cref="Dx.Core.IgnoreSet"/> via
/// <see cref="Dx.Core.IgnoreSetFactory"/>. This includes:
/// </para>
/// <list type="bullet">
/// <item><description>Default exclusion rules (tool-defined directories)</description></item>
/// <item><description>Artifacts directory handling</description></item>
/// <item><description>Build output inclusion policy</description></item>
/// <item><description>User-provided exclusions</description></item>
/// </list>
/// <para>
/// The resulting <see cref="Dx.Core.IgnoreSet"/> is serialized and persisted as part
/// of the session record. After this point, no command is permitted to recompute or
/// reinterpret exclusion rules.
/// </para>
/// <para>
/// The command fails with exit code <c>1</c> if a workspace already exists at or above
/// the target path, preventing accidental nested workspaces.
/// </para>
/// </remarks>
public sealed class InitCommand : DxCommandBase<InitSettings>
{
    /// <summary>
    /// Executes the workspace initialisation logic.
    /// </summary>
    /// <param name="ctx">The Spectre.Console command context.</param>
    /// <param name="s">The parsed initialisation settings.</param>
    /// <returns>
    /// A task that resolves to the process exit code: <c>0</c> on success,
    /// <c>1</c> on a <see cref="DxException"/>, or <c>1</c> for any unexpected error.
    /// </returns>
    public override async Task<int> ExecuteAsync(CommandContext ctx, InitSettings s)
    {
        try
        {
            var root = FindRoot(s.Path);
            var sessionId = s.Session ?? $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var excludes = s.Exclude?.Split(',', StringSplitOptions.RemoveEmptyEntries);

            WorkspaceInitializer.Initialize(root);

            using var conn = DxDatabase.Open(root);
            DxDatabase.Migrate(conn);

            var result = await SessionGenesisCreator.CreateAsync(
                conn,
                root,
                sessionId,
                s.ArtifactsDir,
                excludes,
                s.IncludeBuildOutput);

            AnsiConsole.MarkupLine("[green]Initialized[/] DX workspace");
            AnsiConsole.MarkupLine($" Root: [dim]{Markup.Escape(root)}[/]");
            AnsiConsole.MarkupLine($" Session: [cyan]{Markup.Escape(sessionId)}[/]");
            AnsiConsole.MarkupLine($" Genesis: [yellow]{Markup.Escape(result.GenesisHandle)}[/] ({result.FileCount} files)");

            return 0;
        }
        catch (DxException ex) { return HandleDxException(ex); }
        catch (Exception ex) { return HandleUnexpected(ex); }
    }
}
