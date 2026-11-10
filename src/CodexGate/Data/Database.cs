using Dapper;
using Microsoft.Data.Sqlite;

namespace CodexGate.Data;

/// <summary>Opens SQLite connections and keeps the schema current.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    static Database()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Database(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    /// <summary>Applies every migration newer than PRAGMA user_version, each in its own transaction.</summary>
    public void Migrate()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            connection.Execute(Migrations[i], transaction: tx);
            connection.Execute($"PRAGMA user_version = {i + 1};", transaction: tx);
            tx.Commit();
        }
    }

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE plans (
            id              INTEGER PRIMARY KEY,
            name            TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            max_concurrency INTEGER NOT NULL DEFAULT 2,
            allowed_models  TEXT    NOT NULL DEFAULT '',
            created_at      INTEGER NOT NULL
        );
        CREATE TABLE plan_limits (
            id           INTEGER PRIMARY KEY,
            plan_id      INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
            window_hours INTEGER NOT NULL CHECK (window_hours > 0),
            kind         TEXT    NOT NULL CHECK (kind IN ('tokens', 'usd')),
            amount       REAL    NOT NULL CHECK (amount >= 0)
        );
        CREATE INDEX ix_plan_limits_plan ON plan_limits(plan_id);
        CREATE TABLE users (
            id             INTEGER PRIMARY KEY,
            username       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            display_name   TEXT    NOT NULL DEFAULT '',
            password_hash  TEXT    NOT NULL,
            role           TEXT    NOT NULL CHECK (role IN ('admin', 'user')),
            plan_id        INTEGER REFERENCES plans(id) ON DELETE RESTRICT,
            enabled        INTEGER NOT NULL DEFAULT 1,
            expires_at     INTEGER,
            note           TEXT    NOT NULL DEFAULT '',
            security_stamp TEXT    NOT NULL,
            created_at     INTEGER NOT NULL,
            last_login_at  INTEGER,
            quota_reset_at INTEGER
        );
        CREATE TABLE api_keys (
            id           INTEGER PRIMARY KEY,
            user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            name         TEXT    NOT NULL DEFAULT '',
            prefix       TEXT    NOT NULL,
            hash         BLOB    NOT NULL UNIQUE,
            created_at   INTEGER NOT NULL,
            last_used_at INTEGER,
            revoked_at   INTEGER
        );
        CREATE INDEX ix_api_keys_user ON api_keys(user_id);
        CREATE TABLE usage (
            id               INTEGER PRIMARY KEY,
            user_id          INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            key_id           INTEGER,
            at               INTEGER NOT NULL,
            model            TEXT    NOT NULL,
            upstream         TEXT    NOT NULL DEFAULT '',
            endpoint         TEXT    NOT NULL,
            status           INTEGER NOT NULL,
            input_tokens     INTEGER NOT NULL DEFAULT 0,
            cached_tokens    INTEGER NOT NULL DEFAULT 0,
            output_tokens    INTEGER NOT NULL DEFAULT 0,
            reasoning_tokens INTEGER NOT NULL DEFAULT 0,
            billable_tokens  REAL    NOT NULL DEFAULT 0,
            cost_usd         REAL,
            estimated        INTEGER NOT NULL DEFAULT 0,
            duration_ms      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_usage_user_at ON usage(user_id, at);
        CREATE INDEX ix_usage_at ON usage(at);
        CREATE TABLE prices (
            model        TEXT PRIMARY KEY COLLATE NOCASE,
            input_per_m  REAL NOT NULL CHECK (input_per_m >= 0),
            cached_per_m REAL NOT NULL CHECK (cached_per_m >= 0),
            output_per_m REAL NOT NULL CHECK (output_per_m >= 0)
        );
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
        """
        ALTER TABLE usage ADD COLUMN tool_calls INTEGER NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE plans ADD COLUMN remote_enabled INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE plans ADD COLUMN remote_max_sessions INTEGER NOT NULL DEFAULT 2;
        ALTER TABLE plans ADD COLUMN remote_memory_mb INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE remote_sessions (
            id         INTEGER PRIMARY KEY,
            user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            name       TEXT    NOT NULL,
            dir        TEXT    NOT NULL,
            program    TEXT    NOT NULL CHECK (program IN ('codex', 'shell')),
            created_at INTEGER NOT NULL
        );
        CREATE INDEX ix_remote_sessions_user ON remote_sessions(user_id);
        CREATE TABLE remote_accounts (
            user_id        INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            key_id         INTEGER,
            provisioned_at INTEGER NOT NULL
        );
        """,
        """
        ALTER TABLE plans ADD COLUMN own_login INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE devices (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            name       TEXT    NOT NULL COLLATE NOCASE,
            created_at INTEGER NOT NULL,
            UNIQUE (user_id, name)
        );
        CREATE TABLE device_limits (
            id           INTEGER PRIMARY KEY,
            device_id    INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
            window_hours INTEGER NOT NULL CHECK (window_hours > 0),
            kind         TEXT    NOT NULL CHECK (kind IN ('tokens', 'usd')),
            amount       REAL    NOT NULL CHECK (amount >= 0)
        );
        CREATE INDEX ix_device_limits_device ON device_limits(device_id);
        ALTER TABLE api_keys ADD COLUMN device_id INTEGER REFERENCES devices(id) ON DELETE SET NULL;
        ALTER TABLE usage ADD COLUMN device_id INTEGER;
        CREATE INDEX ix_usage_device_at ON usage(device_id, at);
        CREATE INDEX ix_usage_key ON usage(key_id);
        ALTER TABLE remote_sessions ADD COLUMN device_id INTEGER;
        ALTER TABLE remote_sessions ADD COLUMN key_id INTEGER;
        """,
    ];
}
