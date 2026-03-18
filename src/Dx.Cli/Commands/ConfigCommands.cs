using Dapper;

using Dx.Core;

using Microsoft.Data.Sqlite;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── Shared config settings ────────────────────────────────────────────────────

public abstract class ConfigBaseSettings : CommandSettings
{
    [CommandOption("--global")]
    [Description("Target global config (~/.dx/config.db).")]
    public bool Global { get; init; }

    [CommandOption("--local")]
    [Description("Target local config (<root>/.dx/dx.db). Default.")]
    public bool Local { get; init; }

    [CommandOption("--root <path>")]
    public string? Root { get; init; }
}

// ── Scope resolution ──────────────────────────────────────────────────────────

file static class ConfigScope
{
    public const string Global = "global";
    public const string Local = "local";

    // Valid keys and their defaults — matches the spec table
    public static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["session.require_base"] = "warn",
        ["run.run_timeout"] = "0",
        ["run.allowed_commands"] = "[]",
        ["conflict.on_base_mismatch"] = "reject",
        ["snap.exclude"] = "[]",
        ["snap.include_build_output"] = "false",
        ["encoding.default_encoding"] = "utf-8",
        ["encoding.default_line_endings"] = "preserve",
        ["git.record_git_sha"] = "true",
    };

    // snap.exclude cannot be set globally (would make hashes non-portable)
    public static readonly HashSet<string> LocalOnlyKeys =
        new(StringComparer.Ordinal) { "snap.exclude" };

    public static string Resolve(ConfigBaseSettings s)
        => s.Global ? Global : Local;

    /// <summary>
    /// Opens the appropriate database for config access.
    /// Global config lives in ~/.dx/dx.db; local config lives in &lt;root&gt;/.dx/dx.db.
    /// </summary>
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

// ── dx config get ─────────────────────────────────────────────────────────────

public sealed class ConfigGetSettings : ConfigBaseSettings
{
    [CommandArgument(0, "<key>")]
    [Description("Config key, e.g. conflict.on_base_mismatch")]
    public string Key { get; init; } = "";
}

public sealed class ConfigGetCommand : DxCommandBase<ConfigGetSettings>
{
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

// ── dx config set ─────────────────────────────────────────────────────────────

public sealed class ConfigSetSettings : ConfigBaseSettings
{
    [CommandArgument(0, "<key>")]
    public string Key { get; init; } = "";

    [CommandArgument(1, "<value>")]
    public string Value { get; init; } = "";
}

public sealed class ConfigSetCommand : DxCommandBase<ConfigSetSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, ConfigSetSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var scope = ConfigScope.Resolve(s);

            if (!ConfigScope.Defaults.ContainsKey(s.Key))
            {
                AnsiConsole.MarkupLine($"[red]Unknown config key:[/] {s.Key}");
                AnsiConsole.MarkupLine("[dim]Run 'dx config list' to see valid keys.[/]");
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

// ── dx config unset ───────────────────────────────────────────────────────────

public sealed class ConfigUnsetSettings : ConfigBaseSettings
{
    [CommandArgument(0, "<key>")]
    public string Key { get; init; } = "";
}

public sealed class ConfigUnsetCommand : DxCommandBase<ConfigUnsetSettings>
{
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

// ── dx config list ────────────────────────────────────────────────────────────

public sealed class ConfigListSettings : ConfigBaseSettings { }

public sealed class ConfigListCommand : DxCommandBase<ConfigListSettings>
{
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

// ── dx config show-effective ──────────────────────────────────────────────────

public sealed class ConfigShowEffectiveSettings : CommandSettings
{
    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--session <id>")]
    public string? Session { get; init; }
}

public sealed class ConfigShowEffectiveCommand : DxCommandBase<ConfigShowEffectiveSettings>
{
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
            var dxDb = Path.Combine(root, ".dx", "dx.db");
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
