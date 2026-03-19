using Dapper;

using Dx.Core;

using Microsoft.Data.Sqlite;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── Shared config settings ────────────────────────────────────────────────────

/// <summary>
/// Provides shared base settings for all <c>dxs config</c> sub-commands, defining
/// the configuration scope and workspace root.
/// </summary>
public abstract class ConfigBaseSettings : CommandSettings
{
    /// <summary>
    /// Gets a value indicating whether to target the global configuration store
    /// located at <c>~/.dx/.dx/snap.db</c>.
    /// When neither <c>--global</c> nor <c>--local</c> is specified, local scope is assumed.
    /// </summary>
    [CommandOption("-g|--global")]
    [Description("Target the global config store (~/.dx/.dx/snap.db).")]
    public bool Global { get; init; }

    /// <summary>
    /// Gets a value indicating whether to target the local workspace configuration store
    /// located at <c>&lt;root&gt;/.dx/snap.db</c>.
    /// This is the default scope when no scope flag is provided.
    /// </summary>
    [CommandOption("-l|--local")]
    [Description("Target the local workspace config store (<root>/.dx/snap.db). Default.")]
    public bool Local { get; init; }

    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }
}

// ── Scope resolution ──────────────────────────────────────────────────────────

/// <summary>
/// Internal helper that resolves the effective configuration scope and opens the
/// corresponding SQLite database connection.
/// </summary>
file static class ConfigScope
{
    /// <summary>Identifier for the global configuration scope.</summary>
    public const string Global = "global";

    /// <summary>Identifier for the local workspace configuration scope.</summary>
    public const string Local = "local";

    /// <summary>
    /// Canonical set of all supported configuration keys and their default values.
    /// These match the specification table and are used for validation and fallback display.
    /// </summary>
    public static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["session.require_base"]         = "warn",
        ["run.run_timeout"]              = "0",
        ["run.allowed_commands"]         = "[]",
        ["conflict.on_base_mismatch"]    = "reject",
        ["snap.exclude"]                 = "[]",
        ["snap.include_build_output"]    = "false",
        ["encoding.default_encoding"]    = "utf-8",
        ["encoding.default_line_endings"] = "preserve",
        ["git.record_git_sha"]           = "true",
    };

    /// <summary>
    /// Keys that may only be set at local (workspace) scope.
    /// Setting these globally would make snap hashes non-portable across machines.
    /// </summary>
    public static readonly HashSet<string> LocalOnlyKeys =
        new(StringComparer.Ordinal) { "snap.exclude" };

    /// <summary>
    /// Resolves the target scope string from the provided settings.
    /// Returns <see cref="Global"/> when <c>--global</c> is specified; otherwise <see cref="Local"/>.
    /// </summary>
    /// <param name="s">The base settings for a config command.</param>
    /// <returns>The resolved scope identifier string.</returns>
    public static string Resolve(ConfigBaseSettings s)
        => s.Global ? Global : Local;

    /// <summary>
    /// Opens and migrates the SQLite database appropriate for the given scope.
    /// Global config resides at <c>~/.dx/snap.db</c>; local config at
    /// <c>&lt;root&gt;/.dx/snap.db</c>.
    /// </summary>
    /// <param name="scope">The resolved scope string (<see cref="Global"/> or <see cref="Local"/>).</param>
    /// <param name="root">The workspace root directory (used for local scope).</param>
    /// <returns>An open, migrated <see cref="SqliteConnection"/>.</returns>
    public static SqliteConnection OpenDb(string scope, string root)
    {
        var conn = DxDatabase.Open(
            scope == Global
                ? Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".dx")
                : root);

        DxDatabase.Migrate(conn);
        return conn;
    }
}

// ── dxs config get ────────────────────────────────────────────────────────────

/// <summary>
/// Defines settings for the <c>dxs config get</c> command.
/// </summary>
public sealed class ConfigGetSettings : ConfigBaseSettings
{
    /// <summary>
    /// Gets the configuration key whose value should be retrieved,
    /// for example <c>conflict.on_base_mismatch</c>.
    /// </summary>
    [CommandArgument(0, "<key>")]
    [Description("Configuration key to read, e.g. conflict.on_base_mismatch.")]
    public string Key { get; init; } = "";
}

/// <summary>
/// Implements the <c>dxs config get</c> command, which retrieves and prints a single
/// configuration value from the active scope.
/// </summary>
/// <remarks>
/// When the key has no explicitly stored value the built-in default is printed with
/// a <c>(default)</c> suffix. When the key is unrecognised an error is reported.
/// </remarks>
public sealed class ConfigGetCommand : DxCommandBase<ConfigGetSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, ConfigGetSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var scope = ConfigScope.Resolve(s);

            using var conn = ConfigScope.OpenDb(scope, root);

            var value = conn.ExecuteScalar<string>(
                "SELECT value FROM config WHERE scope = @scope AND key = @key",
                new { scope, key = s.Key });

            if (value is null)
            {
                if (ConfigScope.Defaults.TryGetValue(s.Key, out var def))
                {
                    // Use Console.WriteLine to avoid Markup parsing of values like "[]"
                    Console.WriteLine($"{def} (default)");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]Unknown key:[/] {s.Key}");
                }
            }
            else
            {
                AnsiConsole.WriteLine(value);
            }

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs config set ────────────────────────────────────────────────────────────

/// <summary>
/// Defines settings for the <c>dxs config set</c> command.
/// </summary>
public sealed class ConfigSetSettings : ConfigBaseSettings
{
    /// <summary>
    /// Gets the configuration key to create or update,
    /// for example <c>run.run_timeout</c>.
    /// </summary>
    [CommandArgument(0, "<key>")]
    [Description("Configuration key to set, e.g. run.run_timeout.")]
    public string Key { get; init; } = "";

    /// <summary>
    /// Gets the string value to store for the specified key.
    /// All configuration values are persisted as strings and interpreted at runtime.
    /// </summary>
    [CommandArgument(1, "<value>")]
    [Description("Value to assign to the key.")]
    public string Value { get; init; } = "";
}

/// <summary>
/// Implements the <c>dxs config set</c> command, which upserts a configuration value
/// at the specified scope.
/// </summary>
/// <remarks>
/// Unknown keys and attempts to set a local-only key at global scope are rejected
/// with exit code <c>2</c>.
/// </remarks>
public sealed class ConfigSetCommand : DxCommandBase<ConfigSetSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, ConfigSetSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var scope = ConfigScope.Resolve(s);

            if (!ConfigScope.Defaults.ContainsKey(s.Key))
            {
                AnsiConsole.MarkupLine($"[red]Unknown config key:[/] {s.Key}");
                AnsiConsole.MarkupLine("[dim]Run 'dxs config list' to see valid keys.[/]");
                return Task.FromResult(2);
            }

            if (scope == ConfigScope.Global && ConfigScope.LocalOnlyKeys.Contains(s.Key))
            {
                AnsiConsole.MarkupLine(
                    $"[red]'{s.Key}' cannot be set globally[/] — " +
                    "it would make snap hashes non-portable across machines.");
                return Task.FromResult(2);
            }

            using var conn = ConfigScope.OpenDb(scope, root);

            conn.Execute(
                """
                INSERT INTO config (scope, key, value, updated_at)
                VALUES (@scope, @key, @value, @t)
                ON CONFLICT (scope, key) DO UPDATE
                    SET value      = excluded.value,
                        updated_at = excluded.updated_at
                """,
                new { scope, key = s.Key, value = s.Value, t = DxDatabase.UtcNow() });

            AnsiConsole.MarkupLine(
                $"[green]Set[/] [cyan]{s.Key}[/] = [yellow]{Markup.Escape(s.Value)}[/] " +
                $"[dim]({scope})[/]");

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs config unset ──────────────────────────────────────────────────────────

/// <summary>
/// Defines settings for the <c>dxs config unset</c> command.
/// </summary>
public sealed class ConfigUnsetSettings : ConfigBaseSettings
{
    /// <summary>
    /// Gets the configuration key to remove from the active scope.
    /// After removal, subsequent reads fall back to any wider scope or the built-in default.
    /// </summary>
    [CommandArgument(0, "<key>")]
    [Description("Configuration key to remove from the active scope.")]
    public string Key { get; init; } = "";
}

/// <summary>
/// Implements the <c>dxs config unset</c> command, which deletes a stored configuration
/// value from the active scope.
/// </summary>
/// <remarks>
/// If the key has no stored value at the target scope a dim informational message is
/// printed and the command still exits with code <c>0</c>.
/// </remarks>
public sealed class ConfigUnsetCommand : DxCommandBase<ConfigUnsetSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, ConfigUnsetSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var scope = ConfigScope.Resolve(s);

            using var conn = ConfigScope.OpenDb(scope, root);

            var deleted = conn.Execute(
                "DELETE FROM config WHERE scope = @scope AND key = @key",
                new { scope, key = s.Key });

            if (deleted == 0)
                AnsiConsole.MarkupLine($"[dim]{s.Key} was not set at {scope} scope.[/]");
            else
                AnsiConsole.MarkupLine(
                    $"[green]Unset[/] [cyan]{s.Key}[/] [dim]({scope})[/]");

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs config list ───────────────────────────────────────────────────────────

/// <summary>
/// Defines settings for the <c>dxs config list</c> command.
/// Inherits scope and root selection from <see cref="ConfigBaseSettings"/>.
/// </summary>
public sealed class ConfigListSettings : ConfigBaseSettings { }

/// <summary>
/// Implements the <c>dxs config list</c> command, which displays all explicitly stored
/// configuration values at the active scope in a formatted table.
/// </summary>
/// <remarks>
/// Only values that have been explicitly set are shown. Built-in defaults that have not
/// been overridden do not appear; use <c>dxs config show-effective</c> to see the full
/// resolved configuration including defaults.
/// </remarks>
public sealed class ConfigListCommand : DxCommandBase<ConfigListSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, ConfigListSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var scope = ConfigScope.Resolve(s);

            using var conn = ConfigScope.OpenDb(scope, root);

            var rows = conn.Query<(string Key, string Value, string UpdatedAt)>(
                "SELECT key, value, updated_at FROM config WHERE scope = @scope ORDER BY key",
                new { scope }).ToList();

            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Key")
                .AddColumn("Value")
                .AddColumn(new TableColumn("Updated").RightAligned());

            foreach (var (key, value, updatedAt) in rows)
            {
                var ts = updatedAt.Length > 19
                    ? updatedAt[..19].Replace('T', ' ')
                    : updatedAt;
                // Escape value — default values like "[]" are valid Spectre markup
                table.AddRow($"[cyan]{key}[/]", Markup.Escape(value), $"[dim]{ts}[/]");
            }

            if (rows.Count == 0)
                AnsiConsole.MarkupLine($"[dim]No config values set at {scope} scope.[/]");
            else
                AnsiConsole.Write(table);

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dxs config show-effective ─────────────────────────────────────────────────

/// <summary>
/// Defines settings for the <c>dxs config show-effective</c> command.
/// </summary>
public sealed class ConfigShowEffectiveSettings : CommandSettings
{
    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }

    /// <summary>
    /// Gets the session identifier.
    /// Reserved for future use; currently informational only.
    /// </summary>
    [CommandOption("-s|--session <id>")]
    [Description("Session identifier (informational).")]
    public string? Session { get; init; }
}

/// <summary>
/// Implements the <c>dxs config show-effective</c> command, which merges global, local,
/// and built-in default configuration values according to the precedence rules and displays
/// the fully resolved configuration in a formatted table.
/// </summary>
/// <remarks>
/// <para>Precedence order (highest to lowest):</para>
/// <list type="number">
///   <item><description>Command-line flags</description></item>
///   <item><description>Session scope</description></item>
///   <item><description>Local (workspace) scope</description></item>
///   <item><description>Global scope</description></item>
///   <item><description>Built-in defaults</description></item>
/// </list>
/// </remarks>
public sealed class ConfigShowEffectiveCommand : DxCommandBase<ConfigShowEffectiveSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, ConfigShowEffectiveSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);

            // Load global config
            var globalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dx");
            var globalValues = new Dictionary<string, string>(StringComparer.Ordinal);

            if (Directory.Exists(globalDir))
            {
                using var globalConn = DxDatabase.Open(globalDir);
                DxDatabase.Migrate(globalConn);
                foreach (var row in globalConn.Query<(string Key, string Value)>(
                    "SELECT key, value FROM config WHERE scope = 'global'"))
                    globalValues[row.Key] = row.Value;
            }

            // Load local config
            var localValues = new Dictionary<string, string>(StringComparer.Ordinal);
            var dxDb = Path.Combine(root, ".dx", "snap.db");
            if (File.Exists(dxDb))
            {
                using var localConn = DxDatabase.Open(root);
                foreach (var row in localConn.Query<(string Key, string Value)>(
                    "SELECT key, value FROM config WHERE scope = 'local'"))
                    localValues[row.Key] = row.Value;
            }

            AnsiConsole.MarkupLine(
                "[bold]Effective configuration[/] " +
                "[dim](flag → session → local → global → default)[/]");
            AnsiConsole.WriteLine();

            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Key")
                .AddColumn("Value")
                .AddColumn("Source");

            foreach (var key in ConfigScope.Defaults.Keys.OrderBy(k => k))
            {
                string value, source;

                if (localValues.TryGetValue(key, out var lv))
                {
                    value = lv;
                    source = "[cyan]local[/]";
                }
                else if (globalValues.TryGetValue(key, out var gv))
                {
                    value = gv;
                    source = "[blue]global[/]";
                }
                else
                {
                    value = ConfigScope.Defaults[key];
                    source = "[dim]default[/]";
                }

                // Escape value — default values like "[]" are valid Spectre markup tags
                table.AddRow($"[cyan]{key}[/]", Markup.Escape(value), source);
            }

            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}
