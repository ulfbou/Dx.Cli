using Dapper;

using Dx.Core;

using Microsoft.Data.Sqlite;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

// ── dxs session list ──────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs session list</c> command.
/// </summary>
public sealed class SessionListSettings : CommandSettings
{
    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }
}

/// <summary>
/// Implements the <c>dxs session list</c> command, which displays all sessions registered
/// in the current workspace along with their HEAD snapshot and open/closed status.
/// </summary>
public sealed class SessionListCommand : DxCommandBase<SessionListSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, SessionListSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var dxDb = Path.Combine(root, ".dx", "snap.db");

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

    /// <summary>
    /// Resolves the human-readable snapshot handle for the given raw hash within a session.
    /// Returns a formatted markup string, or <c>[dim]?[/]</c> if the handle cannot be found.
    /// </summary>
    /// <param name="conn">An open database connection to query.</param>
    /// <param name="sessionId">The session identifier to scope the lookup.</param>
    /// <param name="headHash">The raw SHA-256 hash of the HEAD snapshot.</param>
    /// <returns>A Spectre Console markup string representing the handle.</returns>
    private static string ResolveHeadHandle(
        SqliteConnection conn,
        string sessionId,
        byte[] headHash)
    {
        var handle = conn.ExecuteScalar<string>(
            "SELECT handle FROM snap_handles WHERE session_id = @sid AND snap_hash = @h",
            new { sid = sessionId, h = headHash });
        return handle is not null ? $"[yellow]{handle}[/]" : "[dim]?[/]";
    }
}

/// <summary>
/// Internal data-transfer record used by Dapper to map session query results.
/// </summary>
file sealed record SessionRow(
    string SessionId,
    string CreatedUtc,
    string? ClosedUtc,
    byte[]? HeadSnapHash);

// ── dxs session new ───────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs session new</c> command.
/// </summary>
public sealed class SessionNewSettings : CommandSettings
{
    /// <summary>
    /// Gets the identifier to assign to the new session.
    /// Defaults to a UTC timestamp-based identifier (<c>session-yyyyMMdd-HHmmss</c>)
    /// when omitted.
    /// </summary>
    [CommandArgument(0, "[id]")]
    [Description("Session identifier. Defaults to a UTC timestamp.")]
    public string? Id { get; init; }

    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }

    /// <summary>
    /// Gets the path to a directory whose contents should be excluded from all snapshots
    /// taken within this session.
    /// </summary>
    [CommandOption("-a|--artifacts-dir <path>")]
    [Description("Directory excluded from all snaps in this session (e.g. CI artifact output).")]
    public string? ArtifactsDir { get; init; }

    /// <summary>
    /// Gets a comma-separated list of additional paths to exclude from snapshots,
    /// supplementing the built-in exclusion list.
    /// </summary>
    [CommandOption("-x|--exclude <paths>")]
    [Description("Comma-separated additional paths to exclude from snaps.")]
    public string? Exclude { get; init; }

    /// <summary>
    /// Gets a value indicating whether build output directories (<c>bin/</c> and <c>obj/</c>)
    /// should be included in snapshots. Excluded by default.
    /// </summary>
    [CommandOption("-b|--include-build-output")]
    [Description("Include bin/ and obj/ directories in snaps. Excluded by default.")]
    public bool IncludeBuildOutput { get; init; }
}

/// <summary>
/// Implements the <c>dxs session new</c> command, which registers a new session in the
/// current workspace and takes a genesis snapshot <c>T0000</c> of the current working tree.
/// </summary>
/// <remarks>
/// The workspace must already be initialised (<c>dxs init</c>) before this command can be used.
/// The new session does not become the active session automatically; use <c>--session</c>
/// on subsequent commands to target it explicitly.
/// </remarks>
public sealed class SessionNewCommand : DxCommandBase<SessionNewSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, SessionNewSettings s)
    {
        try
        {
            var root = FindRoot(s.Root);
            var sessionId = s.Id ?? $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var excludes = s.Exclude?.Split(',', StringSplitOptions.RemoveEmptyEntries);

            var dxDb = Path.Combine(root, ".dx", "snap.db");
            if (!File.Exists(dxDb))
                throw new DxException(DxError.WorkspaceNotInitialized,
                    $"No DX workspace at {root}. Run 'dxs init' first.");

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
                    sid  = sessionId,
                    root = Path.GetFullPath(root),
                    arts = s.ArtifactsDir,
                    ign  = ignoreSet.Serialize(),
                    t    = DxDatabase.UtcNow()
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
                        sh  = snapHash,
                        p   = entry.Path,
                        ch  = entry.ContentHash,
                        sz  = entry.Size
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

// ── dxs session show ──────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs session show</c> command.
/// </summary>
public sealed class SessionShowSettings : CommandSettings
{
    /// <summary>
    /// Gets the identifier of the session to display.
    /// Defaults to the most recent active session when omitted.
    /// </summary>
    [CommandArgument(0, "[id]")]
    [Description("Session ID to inspect. Defaults to the most recent active session.")]
    public string? Id { get; init; }

    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }
}

/// <summary>
/// Implements the <c>dxs session show</c> command, which displays the HEAD snapshot,
/// total snapshot count, and recent activity for the specified or current session.
/// </summary>
public sealed class SessionShowCommand : DxCommandBase<SessionShowSettings>
{
    /// <inheritdoc />
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

// ── dxs session close ─────────────────────────────────────────────────────────

/// <summary>
/// Defines the settings for the <c>dxs session close</c> command.
/// </summary>
public sealed class SessionCloseSettings : CommandSettings
{
    /// <summary>
    /// Gets the identifier of the session to close.
    /// Defaults to the most recent active session when omitted.
    /// </summary>
    [CommandArgument(0, "[id]")]
    [Description("Session ID to close. Defaults to the most recent active session.")]
    public string? Id { get; init; }

    /// <summary>
    /// Gets the explicit workspace root path.
    /// When omitted, the root is discovered by walking up from the current directory
    /// until a <c>.dx/</c> folder is found.
    /// </summary>
    [CommandOption("-r|--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }
}

/// <summary>
/// Implements the <c>dxs session close</c> command, which marks the specified session as
/// closed so that it no longer appears as the default active session.
/// </summary>
/// <remarks>
/// Closing a session is non-destructive: all snapshots and log entries are retained.
/// A closed session can still be targeted explicitly by passing its identifier to
/// <c>--session</c> on any command.
/// </remarks>
public sealed class SessionCloseCommand : DxCommandBase<SessionCloseSettings>
{
    /// <inheritdoc />
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
