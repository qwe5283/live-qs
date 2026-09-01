using System.Text.Json;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS app_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS application_rules (
                app_id TEXT PRIMARY KEY COLLATE NOCASE,
                alias TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '未分类',
                is_excluded INTEGER NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS activity_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc TEXT NOT NULL,
                ended_utc TEXT NOT NULL,
                last_sample_utc TEXT NOT NULL,
                app_id TEXT NOT NULL COLLATE NOCASE,
                app_name TEXT NOT NULL,
                executable_path TEXT NOT NULL DEFAULT '',
                window_title TEXT NOT NULL DEFAULT '',
                window_title_hash TEXT NOT NULL DEFAULT '',
                is_afk INTEGER NOT NULL DEFAULT 0,
                is_audio_playing INTEGER NOT NULL DEFAULT 0,
                is_fullscreen INTEGER NOT NULL DEFAULT 0,
                battery_percent INTEGER NULL,
                battery_charging INTEGER NULL,
                fingerprint TEXT NOT NULL,
                sync_version INTEGER NOT NULL DEFAULT 1,
                finalized INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_queue (
                segment_id INTEGER PRIMARY KEY REFERENCES activity_segments(id) ON DELETE CASCADE,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc TEXT NOT NULL,
                last_error TEXT NOT NULL DEFAULT '',
                permanent INTEGER NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                last_success_utc TEXT NULL,
                install_guid TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_activity_segments_range
                ON activity_segments(started_utc, ended_utc);
            CREATE INDEX IF NOT EXISTS ix_activity_segments_app
                ON activity_segments(app_id, started_utc);
            CREATE INDEX IF NOT EXISTS ix_activity_segments_timeline
                ON activity_segments(started_utc DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_sync_queue_due
                ON sync_queue(next_attempt_utc, attempt_count);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Columns added after the first release; existing databases migrate in place.
        await AddColumnIfMissingAsync(connection, "activity_segments", "finalized", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, "sync_queue", "permanent", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, "sync_state", "install_guid", "TEXT NULL", cancellationToken);

        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO app_settings(id, json, updated_utc)
            VALUES(1, $json, $now)
            ON CONFLICT(id) DO NOTHING;
            INSERT INTO sync_state(id, last_success_utc)
            VALUES(1, NULL)
            ON CONFLICT(id) DO NOTHING;
            """;
        seed.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new AppSettings(), JsonOptions));
        seed.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        await seed.ExecuteNonQueryAsync(cancellationToken);

        await EnsureInstallGuidAsync(connection, cancellationToken);
    }

    /// <summary>
    /// The install guid scopes event identity: it is stable for the lifetime of
    /// this local store, so retries and checkpoints keep one event id, while a
    /// wiped database starts with fresh identities instead of colliding with
    /// already uploaded history.
    /// </summary>
    private async Task EnsureInstallGuidAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT install_guid FROM sync_state WHERE id = 1;";
        var existing = (await read.ExecuteScalarAsync(cancellationToken)) as string;
        if (!string.IsNullOrWhiteSpace(existing)) return;

        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE sync_state SET install_guid = $guid WHERE id = 1;";
        write.Parameters.AddWithValue("$guid", Guid.NewGuid().ToString());
        await write.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await info.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        if (columns.Contains(column, StringComparer.OrdinalIgnoreCase)) return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
