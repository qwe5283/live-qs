using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveQs.Windows.Core;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Infrastructure;

public sealed class SqliteActivityRepository(IAppPaths paths, TimeProvider timeProvider) : IActivityRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] Palette =
    [
        "#007AFF", "#D56A45", "#4285F4", "#1DB954", "#7A63A8",
        "#4A154B", "#A75B2D", "#A259FF", "#FF6F00", "#526D82",
    ];

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    }.ToString();

    private readonly TimeProvider _timeProvider = timeProvider;

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
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_queue (
                segment_id INTEGER PRIMARY KEY REFERENCES activity_segments(id) ON DELETE CASCADE,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc TEXT NOT NULL,
                last_error TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                last_success_utc TEXT NULL
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
    }

    public async Task RecordSampleAsync(ActivitySample sample, TimeSpan sampleInterval, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var rule = await GetRuleAsync(sample.AppId, cancellationToken);
        if (rule?.IsExcluded == true) return;

        var normalizedInterval = TimeSpan.FromSeconds(Math.Clamp(sampleInterval.TotalSeconds, 1, 300));
        var startedAt = sample.CapturedAt.ToUniversalTime();
        var endedAt = startedAt + normalizedInterval;
        var fingerprint = Fingerprint(sample, settings.WindowTitleMode);
        var now = _timeProvider.GetUtcNow();

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        long? latestId = null;
        DateTimeOffset latestEnd = default;
        string latestFingerprint = "";
        await using (var latest = connection.CreateCommand())
        {
            latest.Transaction = transaction;
            latest.CommandText = """
                SELECT id, ended_utc, fingerprint
                FROM activity_segments
                ORDER BY id DESC
                LIMIT 1;
                """;
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                latestId = reader.GetInt64(0);
                latestEnd = ParseTime(reader.GetString(1));
                latestFingerprint = reader.GetString(2);
            }
        }

        var mergeGap = normalizedInterval + normalizedInterval;
        if (latestId is not null && latestFingerprint == fingerprint && startedAt <= latestEnd + mergeGap)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE activity_segments
                SET ended_utc = $ended, last_sample_utc = $sampled,
                    is_audio_playing = $audio, is_fullscreen = $fullscreen,
                    battery_percent = $battery, battery_charging = $charging,
                    sync_version = sync_version + 1, updated_utc = $now
                WHERE id = $id;
                INSERT INTO sync_queue(segment_id, attempt_count, next_attempt_utc, last_error, updated_utc)
                VALUES($id, 0, $now, '', $now)
                ON CONFLICT(segment_id) DO UPDATE SET
                    attempt_count = 0, next_attempt_utc = excluded.next_attempt_utc,
                    last_error = '', updated_utc = excluded.updated_utc;
                """;
            update.Parameters.AddWithValue("$ended", UtcText(endedAt > latestEnd ? endedAt : latestEnd));
            update.Parameters.AddWithValue("$sampled", UtcText(startedAt));
            update.Parameters.AddWithValue("$audio", sample.IsAudioPlaying);
            update.Parameters.AddWithValue("$fullscreen", sample.IsFullscreen);
            update.Parameters.AddWithValue("$battery", (object?)sample.BatteryPercent ?? DBNull.Value);
            update.Parameters.AddWithValue("$charging", sample.BatteryCharging is null ? DBNull.Value : sample.BatteryCharging.Value);
            update.Parameters.AddWithValue("$now", UtcText(now));
            update.Parameters.AddWithValue("$id", latestId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO activity_segments(
                    started_utc, ended_utc, last_sample_utc, app_id, app_name,
                    executable_path, window_title, window_title_hash, is_afk,
                    is_audio_playing, is_fullscreen, battery_percent, battery_charging,
                    fingerprint, created_utc, updated_utc)
                VALUES(
                    $started, $ended, $sampled, $appId, $appName,
                    $path, $title, $titleHash, $afk,
                    $audio, $fullscreen, $battery, $charging,
                    $fingerprint, $now, $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$started", UtcText(startedAt));
            insert.Parameters.AddWithValue("$ended", UtcText(endedAt));
            insert.Parameters.AddWithValue("$sampled", UtcText(startedAt));
            insert.Parameters.AddWithValue("$appId", sample.AppId);
            insert.Parameters.AddWithValue("$appName", sample.AppName);
            insert.Parameters.AddWithValue("$path", sample.ExecutablePath);
            insert.Parameters.AddWithValue("$title", sample.WindowTitle);
            insert.Parameters.AddWithValue("$titleHash", sample.WindowTitleHash);
            insert.Parameters.AddWithValue("$afk", sample.IsAfk);
            insert.Parameters.AddWithValue("$audio", sample.IsAudioPlaying);
            insert.Parameters.AddWithValue("$fullscreen", sample.IsFullscreen);
            insert.Parameters.AddWithValue("$battery", (object?)sample.BatteryPercent ?? DBNull.Value);
            insert.Parameters.AddWithValue("$charging", sample.BatteryCharging is null ? DBNull.Value : sample.BatteryCharging.Value);
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$now", UtcText(now));
            var segmentId = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? 0L);

            await using var queue = connection.CreateCommand();
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT INTO sync_queue(segment_id, attempt_count, next_attempt_utc, last_error, updated_utc)
                VALUES($id, 0, $now, '', $now);
                """;
            queue.Parameters.AddWithValue("$id", segmentId);
            queue.Parameters.AddWithValue("$now", UtcText(now));
            await queue.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default)
    {
        var segments = await ReadSegmentsAsync(range, cancellationToken);
        var active = TimeSpan.Zero;
        var afk = TimeSpan.Zero;
        var byApp = new Dictionary<string, (string Name, string Category, string Color, TimeSpan Duration)>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            var duration = ClippedDuration(segment.StartedAt, segment.EndedAt, range);
            if (duration <= TimeSpan.Zero) continue;
            if (segment.IsAfk)
            {
                afk += duration;
                continue;
            }

            active += duration;
            var key = segment.AppId;
            if (!byApp.TryGetValue(key, out var current))
                current = (segment.AppName, segment.Category, segment.Color, TimeSpan.Zero);
            byApp[key] = (current.Name, current.Category, current.Color, current.Duration + duration);
        }

        var apps = byApp
            .Select(item => new AppUsage(item.Key, item.Value.Name, item.Value.Category, item.Value.Duration,
                active > TimeSpan.Zero ? item.Value.Duration.TotalSeconds / active.TotalSeconds : 0, item.Value.Color))
            .OrderByDescending(item => item.Duration)
            .ToArray();
        return new DashboardSnapshot(range.Start, range.End, active, afk, apps.Length, apps);
    }

    public async Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default) =>
        await ReadSegmentsAsync(range, cancellationToken);

    public async Task<TimelinePage> GetTimelinePageAsync(
        DateRange range,
        int pageSize,
        TimelineCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(pageSize, 1, 1_000);
        var items = await ReadSegmentsPageAsync(range, take + 1, cursor, cancellationToken);
        var hasMore = items.Count > take;
        var pageItems = hasMore ? items.Take(take).ToArray() : items;
        var last = pageItems.LastOrDefault();
        TimelineCursor? nextCursor = hasMore && last is not null ? new TimelineCursor(last.StartedAt, last.Id) : null;
        return new TimelinePage(pageItems, nextCursor, hasMore);
    }

    public async Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT known.app_id,
                   COALESCE(r.alias, ''),
                   COALESCE(r.category, '未分类'),
                   COALESCE(r.is_excluded, 0)
            FROM (SELECT DISTINCT app_id FROM activity_segments) known
            LEFT JOIN application_rules r ON r.app_id = known.app_id
            ORDER BY known.app_id COLLATE NOCASE;
            """;
        var result = new List<ApplicationRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ApplicationRule(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return result;
    }

    public async Task SaveApplicationRuleAsync(ApplicationRule rule, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO application_rules(app_id, alias, category, is_excluded, updated_utc)
            VALUES($appId, $alias, $category, $excluded, $now)
            ON CONFLICT(app_id) DO UPDATE SET
                alias = excluded.alias, category = excluded.category,
                is_excluded = excluded.is_excluded, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$appId", rule.AppId.Trim());
        command.Parameters.AddWithValue("$alias", rule.Alias.Trim());
        command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(rule.Category) ? "未分类" : rule.Category.Trim());
        command.Parameters.AddWithValue("$excluded", rule.IsExcluded);
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM app_settings WHERE id = 1;";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try { return (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings()).Normalize(); }
        catch (JsonException) { return new AppSettings(); }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        var error = normalized.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(settings));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(id, json, updated_utc)
            VALUES(1, $json, $now)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(normalized, JsonOptions));
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncQueueItem>> GetPendingSyncAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, q.attempt_count, s.started_utc, s.ended_utc,
                   s.app_id, s.app_name, s.window_title, s.window_title_hash,
                   s.is_afk, s.is_audio_playing, s.is_fullscreen
            FROM sync_queue q
            JOIN activity_segments s ON s.id = q.segment_id
            WHERE q.next_attempt_utc <= $now
            ORDER BY q.next_attempt_utc, s.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", UtcText(now));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var result = new List<SyncQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncQueueItem(
                reader.GetInt64(0), reader.GetInt32(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10)));
        }
        return result;
    }

    public async Task MarkSyncedAsync(IEnumerable<long> segmentIds, DateTimeOffset syncedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var id in segmentIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM sync_queue WHERE segment_id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = "UPDATE sync_state SET last_success_utc = $now WHERE id = 1;";
        state.Parameters.AddWithValue("$now", UtcText(syncedAt));
        await state.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task MarkSyncFailedAsync(IEnumerable<long> segmentIds, string error, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var id in segmentIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_queue
                SET attempt_count = attempt_count + 1, next_attempt_utc = $next,
                    last_error = $error, updated_utc = $now
                WHERE segment_id = $id;
                """;
            command.Parameters.AddWithValue("$next", UtcText(nextAttemptAt));
            command.Parameters.AddWithValue("$error", error.Length > 500 ? error[..500] : error);
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<int> GetPendingSyncCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sync_queue;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM activity_segments WHERE ended_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", UtcText(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteRangeAsync(DateRange range, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM activity_segments WHERE started_utc < $end AND ended_utc > $start;";
        command.Parameters.AddWithValue("$start", UtcText(range.Start));
        command.Parameters.AddWithValue("$end", UtcText(range.End));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ExportCsvAsync(string path, DateRange range, CancellationToken cancellationToken = default)
    {
        var rows = await ReadSegmentsAsync(range, cancellationToken);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("开始时间,结束时间,时长秒,应用ID,应用名称,分类,窗口标题,AFK,音频,全屏".AsMemory(), cancellationToken);
        foreach (var row in rows)
        {
            var values = new[]
            {
                row.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                row.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ((long)row.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                row.AppId, row.AppName, row.Category, row.WindowTitle,
                row.IsAfk ? "是" : "否", row.IsAudioPlaying ? "是" : "否", row.IsFullscreen ? "是" : "否",
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(Csv)).AsMemory(), cancellationToken);
        }
    }

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ActivitySegment>> ReadSegmentsAsync(DateRange range, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.started_utc, s.ended_utc, s.app_id,
                   COALESCE(NULLIF(r.alias, ''), s.app_name) AS display_name,
                   s.window_title, s.is_afk, s.is_audio_playing, s.is_fullscreen,
                   COALESCE(NULLIF(r.category, ''), '未分类') AS category
            FROM activity_segments s
            LEFT JOIN application_rules r ON r.app_id = s.app_id
            WHERE s.started_utc < $end AND s.ended_utc > $start
              AND COALESCE(r.is_excluded, 0) = 0
            ORDER BY s.started_utc DESC;
            """;
        command.Parameters.AddWithValue("$start", UtcText(range.Start));
        command.Parameters.AddWithValue("$end", UtcText(range.End));
        var result = new List<ActivitySegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var appId = reader.GetString(3);
            result.Add(new ActivitySegment(
                reader.GetInt64(0), ParseTime(reader.GetString(1)), ParseTime(reader.GetString(2)), appId,
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7),
                reader.GetBoolean(8), reader.GetString(9), ColorFor(appId)));
        }
        return result;
    }

    private async Task<IReadOnlyList<ActivitySegment>> ReadSegmentsPageAsync(
        DateRange range,
        int take,
        TimelineCursor? cursor,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = cursor is null
            ? """
                SELECT s.id, s.started_utc, s.ended_utc, s.app_id,
                       COALESCE(NULLIF(r.alias, ''), s.app_name) AS display_name,
                       s.window_title, s.is_afk, s.is_audio_playing, s.is_fullscreen,
                       COALESCE(NULLIF(r.category, ''), '未分类') AS category
                FROM activity_segments s
                LEFT JOIN application_rules r ON r.app_id = s.app_id
                WHERE s.started_utc < $end AND s.ended_utc > $start
                  AND COALESCE(r.is_excluded, 0) = 0
                ORDER BY s.started_utc DESC, s.id DESC
                LIMIT $take;
                """
            : """
                SELECT s.id, s.started_utc, s.ended_utc, s.app_id,
                       COALESCE(NULLIF(r.alias, ''), s.app_name) AS display_name,
                       s.window_title, s.is_afk, s.is_audio_playing, s.is_fullscreen,
                       COALESCE(NULLIF(r.category, ''), '未分类') AS category
                FROM activity_segments s
                LEFT JOIN application_rules r ON r.app_id = s.app_id
                WHERE s.started_utc < $end AND s.ended_utc > $start
                  AND COALESCE(r.is_excluded, 0) = 0
                  AND (s.started_utc < $cursorStarted
                       OR (s.started_utc = $cursorStarted AND s.id < $cursorId))
                ORDER BY s.started_utc DESC, s.id DESC
                LIMIT $take;
                """;
        command.Parameters.AddWithValue("$start", UtcText(range.Start));
        command.Parameters.AddWithValue("$end", UtcText(range.End));
        command.Parameters.AddWithValue("$take", take);
        if (cursor is { } value)
        {
            command.Parameters.AddWithValue("$cursorStarted", UtcText(value.StartedAt));
            command.Parameters.AddWithValue("$cursorId", value.Id);
        }

        var result = new List<ActivitySegment>(take);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var appId = reader.GetString(3);
            result.Add(new ActivitySegment(
                reader.GetInt64(0), ParseTime(reader.GetString(1)), ParseTime(reader.GetString(2)), appId,
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7),
                reader.GetBoolean(8), reader.GetString(9), ColorFor(appId)));
        }
        return result;
    }

    private async Task<ApplicationRule?> GetRuleAsync(string appId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id, alias, category, is_excluded FROM application_rules WHERE app_id = $appId;";
        command.Parameters.AddWithValue("$appId", appId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ApplicationRule(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static TimeSpan ClippedDuration(DateTimeOffset startedAt, DateTimeOffset endedAt, DateRange range)
    {
        var start = startedAt > range.Start ? startedAt : range.Start;
        var end = endedAt < range.End ? endedAt : range.End;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private static string Fingerprint(ActivitySample sample, WindowTitleMode mode)
    {
        var title = mode switch
        {
            WindowTitleMode.Original => sample.WindowTitle,
            WindowTitleMode.Hash => sample.WindowTitleHash,
            _ => "",
        };
        var value = $"{sample.AppId}\n{title}\n{sample.IsAfk}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ColorFor(string appId)
    {
        uint hash = 2166136261;
        foreach (var character in appId)
        {
            hash ^= char.ToLowerInvariant(character);
            hash *= 16777619;
        }
        return Palette[hash % Palette.Length];
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string UtcText(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
