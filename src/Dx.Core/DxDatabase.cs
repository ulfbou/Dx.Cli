using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

/// <summary>
/// Provides centralised management of SQLite database connections, performance pragmas,
/// and schema migrations for the DX workspace.
/// </summary>
/// <remarks>
/// <para>
/// All DX state — sessions, snapshots, file content, configuration, and the session log —
/// is stored in a single SQLite database file named <c>snap.db</c> inside the <c>.dx/</c>
/// directory. The filename is intentionally distinct from names used by sister tools that
/// may share the same <c>.dx/</c> folder.
/// </para>
/// <para>
/// This class is stateless and all methods are thread-safe with respect to their arguments;
/// however, the returned <see cref="SqliteConnection"/> instances are not thread-safe and
/// must be used from a single thread or protected by appropriate synchronisation.
/// </para>
/// </remarks>
public static class DxDatabase
{
    /// <summary>
    /// Returns the current UTC instant formatted as an ISO 8601 round-trip string
    /// (e.g. <c>2026-03-20T14:23:00.0000000Z</c>).
    /// Used as the canonical timestamp representation throughout the database schema.
    /// </summary>
    /// <returns>An ISO 8601 UTC timestamp string.</returns>
    public static string UtcNow()
        => DateTime.UtcNow.ToString("O");

    /// <summary>
    /// Opens a SQLite connection to the specified database file inside the workspace
    /// <c>.dx/</c> directory, creating the directory if it does not yet exist.
    /// </summary>
    /// <param name="root">
    /// The workspace root directory. The database file will be located at
    /// <c>&lt;root&gt;/.dx/&lt;dbName&gt;</c>.
    /// </param>
    /// <param name="dbName">
    /// The filename of the database within the <c>.dx/</c> directory.
    /// Defaults to <c>snap.db</c>, which is the primary DX workspace database.
    /// Sister tools that share the <c>.dx/</c> folder use different filenames to
    /// avoid conflicts.
    /// </param>
    /// <returns>
    /// An open <see cref="SqliteConnection"/> with WAL journal mode, foreign-key
    /// enforcement, and tuned performance pragmas already applied.
    /// </returns>
    public static SqliteConnection Open(string root, string dbName = "snap.db")
    {
        var dbPath = Path.Combine(root, ".dx", dbName);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    /// <summary>
    /// Opens the global DX configuration database at <c>~/.dx/snap.db</c>,
    /// creating the directory if it does not yet exist.
    /// </summary>
    /// <remarks>
    /// This method exists as a dedicated helper because <see cref="Open"/> appends
    /// <c>.dx/snap.db</c> to whatever root is passed; passing <c>~/.dx</c> to it would
    /// therefore produce <c>~/.dx/.dx/snap.db</c>, contradicting the documented path.
    /// Always use this method when targeting the global configuration store.
    /// </remarks>
    /// <returns>
    /// An open <see cref="SqliteConnection"/> with WAL journal mode, foreign-key
    /// enforcement, and tuned performance pragmas already applied.
    /// </returns>
    public static SqliteConnection OpenGlobal()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dx");
        Directory.CreateDirectory(dir);

        var conn = new SqliteConnection(
            $"Data Source={Path.Combine(dir, "snap.db")}");
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    /// <summary>
    /// Applies the standard set of SQLite performance and correctness pragmas to an
    /// already-open connection.
    /// </summary>
    /// <param name="conn">The open connection to configure.</param>
    private static void ApplyPragmas(SqliteConnection conn)
    {
        conn.Execute("PRAGMA journal_mode = WAL;");
        conn.Execute("PRAGMA foreign_keys = ON;");
        conn.Execute("PRAGMA busy_timeout = 5000;");
        conn.Execute("PRAGMA synchronous = NORMAL;");
        conn.Execute("PRAGMA temp_store = MEMORY;");
        conn.Execute("PRAGMA cache_size = -8000;");
    }

    /// <summary>
    /// Applies all pending schema migrations to the provided connection, bringing the
    /// database up to the current schema version.
    /// </summary>
    /// <param name="conn">
    /// The open connection whose schema should be migrated. The connection must already
    /// be open; <see cref="Open"/> is the recommended way to obtain it.
    /// </param>
    /// <remarks>
    /// Each migration is applied inside its own transaction and recorded in the
    /// <c>schema_version</c> table. Migrations that have already been applied are
    /// skipped, making this method safe to call on every startup (idempotent).
    /// </remarks>
    public static void Migrate(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version    INTEGER NOT NULL,
                applied_at TEXT    NOT NULL
            )
            """);

        var applied = conn.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(version), 0) FROM schema_version");

        foreach (var m in Migrations.All.Where(m => m.Version > applied))
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(m.Sql, transaction: tx);
            conn.Execute(
                "INSERT INTO schema_version (version, applied_at) VALUES (@v, @t)",
                new { v = m.Version, t = UtcNow() }, tx);
            tx.Commit();
        }
    }
}

/// <summary>
/// Represents a single versioned schema migration consisting of a version number and
/// the SQL DDL to execute when applying it.
/// </summary>
file sealed record Migration(int Version, string Sql);

/// <summary>
/// Holds the ordered list of all schema migrations that <see cref="DxDatabase.Migrate"/>
/// will apply in ascending version order.
/// </summary>
file static class Migrations
{
    /// <summary>Gets the complete ordered list of schema migrations.</summary>
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, V1Schema)
    ];

    /// <summary>
    /// Version 1 DDL: creates all core tables and indexes for sessions, snapshots,
    /// file content, the session log, configuration, and the pending-transaction guard.
    /// </summary>
    private const string V1Schema = """
        CREATE TABLE IF NOT EXISTS sessions (
            session_id      TEXT    PRIMARY KEY,
            root            TEXT    NOT NULL,
            artifacts_dir   TEXT,
            ignore_set_json TEXT    NOT NULL DEFAULT '[]',
            created_utc     TEXT    NOT NULL,
            closed_utc      TEXT
        );

        CREATE TABLE IF NOT EXISTS session_state (
            session_id      TEXT    PRIMARY KEY,
            head_snap_hash  BLOB    NOT NULL,
            updated_utc     TEXT    NOT NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS snaps (
            snap_hash   BLOB    PRIMARY KEY,
            created_utc TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS snap_handles (
            session_id  TEXT    NOT NULL,
            handle      TEXT    NOT NULL,
            snap_hash   BLOB    NOT NULL,
            seq         INTEGER NOT NULL,
            created_utc TEXT    NOT NULL,
            PRIMARY KEY (session_id, handle),
            UNIQUE (session_id, snap_hash),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
            FOREIGN KEY (snap_hash)  REFERENCES snaps(snap_hash)     ON DELETE RESTRICT
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_snap_handles_session_seq
            ON snap_handles(session_id, seq);

        CREATE TABLE IF NOT EXISTS snap_files (
            session_id      TEXT    NOT NULL,
            snap_hash       BLOB    NOT NULL,
            path            TEXT    NOT NULL,
            content_hash    BLOB    NOT NULL,
            size_bytes      INTEGER NOT NULL,
            PRIMARY KEY (session_id, snap_hash, path),
            FOREIGN KEY (session_id)   REFERENCES sessions    (session_id)    ON DELETE CASCADE,
            FOREIGN KEY (snap_hash)    REFERENCES snaps       (snap_hash)     ON DELETE CASCADE,
            FOREIGN KEY (content_hash) REFERENCES file_content(content_hash)  ON DELETE RESTRICT
        );

        CREATE INDEX IF NOT EXISTS idx_snap_files_snap
            ON snap_files(snap_hash);

        CREATE INDEX IF NOT EXISTS idx_snap_files_content
            ON snap_files(content_hash);

        CREATE TABLE IF NOT EXISTS file_content (
            content_hash BLOB    PRIMARY KEY,
            content      BLOB    NOT NULL,
            size_bytes   INTEGER NOT NULL,
            inserted_at  TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS session_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id  TEXT    NOT NULL,
            direction   TEXT    NOT NULL CHECK (direction IN ('llm','tool')),
            document    TEXT    NOT NULL,
            snap_handle TEXT,
            tx_success  INTEGER NOT NULL CHECK (tx_success IN (0,1)),
            created_at  TEXT    NOT NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT
        );

        CREATE INDEX IF NOT EXISTS idx_session_log_session
            ON session_log(session_id, id);

        CREATE TABLE IF NOT EXISTS config (
            scope      TEXT NOT NULL,
            key        TEXT NOT NULL,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (scope, key)
        );

        CREATE TABLE IF NOT EXISTS pending_transaction (
            id               INTEGER PRIMARY KEY CHECK (id = 1),
            session_id       TEXT    NOT NULL,
            target_snap_hash BLOB,
            pre_state_json   TEXT    NOT NULL DEFAULT '[]',
            started_utc      TEXT    NOT NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(session_id)
        );
        """;
}
