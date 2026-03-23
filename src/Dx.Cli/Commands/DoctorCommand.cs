using Dapper;

using Dx.Core;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;

namespace Dx.Cli.Commands;

/// <summary>Defines the settings for the <c>dxs doctor</c> command.</summary>
public sealed class DoctorSettings : CommandSettings
{
    /// <summary>Gets the explicit workspace root path.</summary>
    [CommandOption("--root <path>")]
    [Description("Override workspace root. Defaults to nearest ancestor containing a .dx/ folder.")]
    public string? Root { get; init; }

    /// <summary>Gets a value indicating whether to attempt automatic repair.</summary>
    [CommandOption("--repair")]
    [Description("Attempt to automatically repair detected issues.")]
    public bool Repair { get; init; }
}

/// <summary>
/// Implements the <c>dxs doctor</c> command, which inspects the workspace for
/// known problem states and optionally repairs them.
/// </summary>
public sealed class DoctorCommand : DxCommandBase<DoctorSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext ctx, DoctorSettings s)
    {
        var root  = FindRoot(s.Root);
        var dxDir = Path.Combine(root, ".dx");

        if (!Directory.Exists(dxDir))
        {
            Console.Error.WriteLine($"error: No DX workspace found at {root}. Run 'dxs init' first.");
            return Task.FromResult(2);
        }

        var issues  = new List<string>();
        var repairs = new List<string>();

        // ── Check 1: stale lock file ──────────────────────────────────────
        var lockFile = Path.Combine(dxDir, "snaps.lock");
        if (File.Exists(lockFile))
        {
            try
            {
                using var fs = new FileStream(
                    lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                issues.Add("Stale lock file: .dx/snaps.lock (no process holds it)");
                if (s.Repair)
                {
                    fs.Close();
                    File.Delete(lockFile);
                    repairs.Add("Deleted stale lock file .dx/snaps.lock");
                }
            }
            catch (IOException)
            {
                // Lock held by another process — not stale
                Console.Error.WriteLine("  info: .dx/snaps.lock is held by an active process.");
            }
        }

        // ── Check 2: stuck pending transaction ───────────────────────────
        var dbPath = Path.Combine(dxDir, "snap.db");
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = DxDatabase.Open(root);

                var pending = conn.QuerySingleOrDefault<PendingRow>(
                    "SELECT id, session_id, started_utc AS StartedUtc FROM pending_transaction WHERE id = 1");

                if (pending is not null)
                {
                    issues.Add($"Stuck pending transaction: session='{pending.SessionId}' started={pending.StartedUtc}");
                    if (s.Repair)
                    {
                        conn.Execute("DELETE FROM pending_transaction WHERE id = 1");
                        repairs.Add($"Cleared stuck pending transaction for session '{pending.SessionId}'");
                    }
                }

                // ── Check 3: oversized WAL ────────────────────────────────
                var walPath = dbPath + "-wal";
                if (File.Exists(walPath))
                {
                    var walSize = new FileInfo(walPath).Length;
                    if (walSize > 50 * 1024 * 1024)
                    {
                        issues.Add($"WAL file is large ({walSize / 1024 / 1024} MB) — checkpoint recommended");
                        if (s.Repair)
                        {
                            conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                            repairs.Add("Ran WAL checkpoint (TRUNCATE)");
                        }
                    }
                }

                // ── Check 4: HEAD / snap_handles consistency ──────────────
                var orphaned = conn.Query<string>(
                    """
                    SELECT ss.session_id
                    FROM session_state ss
                    LEFT JOIN snap_handles sh
                        ON sh.session_id = ss.session_id
                       AND sh.snap_hash  = ss.head_snap_hash
                    WHERE sh.handle IS NULL
                    """).ToList();

                foreach (var sid in orphaned)
                    issues.Add($"Session '{sid}': HEAD hash has no snap handle (possible corruption)");
            }
            catch (Exception ex)
            {
                issues.Add($"Database error: {ex.Message}");
            }
        }
        else
        {
            issues.Add("Workspace database snap.db not found");
        }

        // ── Report ────────────────────────────────────────────────────────
        if (issues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]  Workspace is healthy.[/]");
            return Task.FromResult(0);
        }

        Console.Error.WriteLine($"  {issues.Count} issue(s) found:");
        foreach (var issue in issues)
            Console.Error.WriteLine($"  - {issue}");

        if (!s.Repair)
        {
            Console.Error.WriteLine("\n  Run 'dxs doctor --repair' to attempt automatic repair.");
            return Task.FromResult(1);
        }

        if (repairs.Count > 0)
        {
            Console.WriteLine($"\n  {repairs.Count} repair(s) applied:");
            foreach (var r in repairs)
                Console.WriteLine($"  + {r}");
            AnsiConsole.MarkupLine("\n[green]  Workspace repaired.[/] Run 'dxs doctor' to verify.");
            return Task.FromResult(0);
        }

        Console.Error.WriteLine("\n  No automatic repairs available for the detected issues.");
        return Task.FromResult(1);
    }

    private sealed record PendingRow(int Id, string SessionId, string StartedUtc);
}
