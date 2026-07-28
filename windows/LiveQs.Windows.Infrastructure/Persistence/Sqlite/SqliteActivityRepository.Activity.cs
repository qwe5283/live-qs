using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
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
}
