using Dapper;

using Microsoft.Data.Sqlite;

namespace Dx.Core;

public static class DxDatabase
{
    public static string UtcNow()
        => DateTime.UtcNow.ToString("O");

    public static SqliteConnection Open(string root)
    {
        var dbPath = Path.Combine(root, ".dx", "dx.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    private static void ApplyPragmas(SqliteConnection conn)
    {
        conn.Execute("PRAGMA journal_mode = WAL;");
        conn.Execute("PRAGMA foreign_keys = ON;");
        conn.Execute("PRAGMA busy_timeout = 5000;");
        conn.Execute("PRAGMA synchronous = NORMAL;");
        conn.Execute("PRAGMA temp_store = MEMORY;");
        conn.Execute("PRAGMA cache_size = -8000;");
    }

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

file sealed record Migration(int Version, string Sql);

file static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, V1Schema)
    ];

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
