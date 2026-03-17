using Dapper;

using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── dx session list ───────────────────────────────────────────────────────────

public sealed class SessionListSettings : CommandSettings
{
    [CommandOption("--root <path>")]
    public string? Root { get; init; }
}

public sealed class SessionListCommand : DxCommandBase<SessionListSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SessionListSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var dxDb = Path.Combine(root, ".dx", "dx.db");

            if (!File.Exists(dxDb))
                throw new DxException(DxError.WorkspaceNotInitialized,
                    $"No DX workspace found at {root}.");

            using var conn = DxDatabase.Open(root);

            var sessions = conn.Query<SessionRow>(
                """
                SELECT s.session_id  AS SessionId,
                       s.created_utc AS CreatedUtc,
                       s.closed_utc  AS ClosedUtc,
                       ss.head_snap_hash AS HeadSnapHash
                FROM sessions s
                LEFT JOIN session_state ss ON ss.session_id = s.session_id
                ORDER BY s.created_utc DESC
                """).ToList();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Session")
                .AddColumn("Created")
                .AddColumn("HEAD")
                .AddColumn("Status");

            foreach (var row in sessions)
            {
                var head = row.HeadSnapHash is null ? "[dim]—[/]"
                    : ResolveHeadHandle(conn, row.SessionId, row.HeadSnapHash);

                var status = row.ClosedUtc is null
                    ? "[green]active[/]" : "[dim]closed[/]";

                var ts = row.CreatedUtc.Length > 19
                    ? row.CreatedUtc[..19].Replace('T', ' ')
                    : row.CreatedUtc;

                table.AddRow(
                    $"[cyan]{row.SessionId}[/]",
                    $"[dim]{ts}[/]",
                    head,
                    status);
            }

            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }

    private static string ResolveHeadHandle(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        string sessionId,
        byte[] headHash)
    {
        var handle = conn.ExecuteScalar<string>(
            "SELECT handle FROM snap_handles WHERE session_id = @sid AND snap_hash = @h",
            new { sid = sessionId, h = headHash });
        return handle is not null ? $"[yellow]{handle}[/]" : "[dim]?[/]";
    }
}

file sealed record SessionRow(
    string SessionId,
    string CreatedUtc,
    string? ClosedUtc,
    byte[]? HeadSnapHash);

// ── dx session new ────────────────────────────────────────────────────────────

public sealed class SessionNewSettings : CommandSettings
{
    [CommandArgument(0, "[id]")]
    [Description("Session identifier. Defaults to timestamp.")]
    public string? Id { get; init; }

    [CommandOption("--root <path>")]
    public string? Root { get; init; }

    [CommandOption("--artifacts-dir <path>")]
    public string? ArtifactsDir { get; init; }

    [CommandOption("--exclude <paths>")]
    [Description("Comma-separated additional paths to exclude.")]
    public string? Exclude { get; init; }

    [CommandOption("--include-build-output")]
    public bool IncludeBuildOutput { get; init; }
}

public sealed class SessionNewCommand : DxCommandBase<SessionNewSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SessionNewSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var sessionId = s.Id ?? $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var excludes = s.Exclude?.Split(',', StringSplitOptions.RemoveEmptyEntries);

            var dxDb = Path.Combine(root, ".dx", "dx.db");
            if (!File.Exists(dxDb))
                throw new DxException(DxError.WorkspaceNotInitialized,
                    $"No DX workspace at {root}. Run 'dx init' first.");

            using var conn = DxDatabase.Open(root);
            DxDatabase.Migrate(conn);

            var exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sessions WHERE session_id = @sid",
                new { sid = sessionId });
            if (exists > 0)
                throw new DxException(DxError.InvalidArgument,
                    $"Session already exists: {sessionId}");

            var ignoreSet = IgnoreSet.Build(
                root, s.ArtifactsDir, excludes, s.IncludeBuildOutput);

            conn.Execute(
                """
                INSERT INTO sessions (session_id, root, artifacts_dir, ignore_set_json, created_utc)
                VALUES (@sid, @root, @arts, @ign, @t)
                """,
                new
                {
                    sid = sessionId,
                    root = Path.GetFullPath(root),
                    arts = s.ArtifactsDir,
                    ign = ignoreSet.Serialize(),
                    t = DxDatabase.UtcNow()
                });

            var manifest = ManifestBuilder.Build(root, ignoreSet);
            var snapHash = ManifestBuilder.ComputeSnapHash(manifest);

            using var tx = conn.BeginTransaction();

            conn.Execute(
                "INSERT OR IGNORE INTO snaps (snap_hash, created_utc) VALUES (@h, @t)",
                new { h = snapHash, t = DxDatabase.UtcNow() }, tx);

            foreach (var entry in manifest)
                BlobStore.InsertFile(conn, tx, entry.AbsolutePath);

            foreach (var entry in manifest)
                conn.Execute(
                    """
                    INSERT OR IGNORE INTO snap_files
                        (session_id, snap_hash, path, content_hash, size_bytes)
                    VALUES (@sid, @sh, @p, @ch, @sz)
                    """,
                    new
                    {
                        sid = sessionId,
                        sh = snapHash,
                        p = entry.Path,
                        ch = entry.ContentHash,
                        sz = entry.Size
                    }, tx);

            HandleAssigner.AssignHandle(conn, tx, sessionId, snapHash, DxDatabase.UtcNow());

            conn.Execute(
                """
                INSERT INTO session_state (session_id, head_snap_hash, updated_utc)
                VALUES (@sid, @sh, @t)
                """,
                new { sid = sessionId, sh = snapHash, t = DxDatabase.UtcNow() }, tx);

            tx.Commit();

            AnsiConsole.MarkupLine("[green]New session started[/]");
            AnsiConsole.MarkupLine($"  Session: [cyan]{sessionId}[/]");
            AnsiConsole.MarkupLine($"  Genesis: [yellow]T0000[/] ({manifest.Count} files)");

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dx session show ───────────────────────────────────────────────────────────

public sealed class SessionShowSettings : CommandSettings
{
    [CommandArgument(0, "[id]")]
    [Description("Session ID. Defaults to most recent active session.")]
    public string? Id { get; init; }

    [CommandOption("--root <path>")]
    public string? Root { get; init; }
}

public sealed class SessionShowCommand : DxCommandBase<SessionShowSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SessionShowSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var runtime = DxRuntime.Open(root, s.Id);
            var head = runtime.GetHead();
            var snaps = runtime.ListSnaps();
            var log = runtime.GetLog(limit: 5);

            AnsiConsole.MarkupLine("[bold]Session[/]");
            AnsiConsole.MarkupLine($"  HEAD:  [yellow]{head ?? "(none)"}[/]");
            AnsiConsole.MarkupLine($"  Snaps: {snaps.Count}");

            if (log.Count > 0)
            {
                AnsiConsole.MarkupLine("");
                AnsiConsole.MarkupLine("[bold]Recent activity[/]");
                foreach (var e in log)
                {
                    var snap = e.SnapHandle is not null
                        ? $"[yellow]{e.SnapHandle}[/]" : "[dim]—[/]";
                    var ok = e.TxSuccess == 1 ? "[green]✓[/]" : "[red]✗[/]";
                    var ts = e.CreatedAt.Length > 19
                        ? e.CreatedAt[..19].Replace('T', ' ') : e.CreatedAt;
                    AnsiConsole.MarkupLine($"  {ok} {snap} [dim]{ts}[/]");
                }
            }

            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}

// ── dx session close ──────────────────────────────────────────────────────────

public sealed class SessionCloseSettings : CommandSettings
{
    [CommandArgument(0, "[id]")]
    public string? Id { get; init; }

    [CommandOption("--root <path>")]
    public string? Root { get; init; }
}

public sealed class SessionCloseCommand : DxCommandBase<SessionCloseSettings>
{
    public override Task<int> ExecuteAsync(CommandContext ctx, SessionCloseSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            using var conn = DxDatabase.Open(root);

            var sessionId = s.Id ?? conn.ExecuteScalar<string>(
                "SELECT session_id FROM sessions WHERE closed_utc IS NULL ORDER BY created_utc DESC LIMIT 1")
                ?? throw new DxException(DxError.SessionNotFound, "No active session.");

            var updated = conn.Execute(
                "UPDATE sessions SET closed_utc = @t WHERE session_id = @sid AND closed_utc IS NULL",
                new { t = DxDatabase.UtcNow(), sid = sessionId });

            if (updated == 0)
                throw new DxException(DxError.SessionNotFound,
                    $"Session not found or already closed: {sessionId}");

            AnsiConsole.MarkupLine($"[green]Closed[/] session [cyan]{sessionId}[/]");
            return Task.FromResult(0);
        }
        catch (DxException ex) { return Task.FromResult(HandleDxException(ex)); }
        catch (Exception ex) { return Task.FromResult(HandleUnexpected(ex)); }
    }
}
