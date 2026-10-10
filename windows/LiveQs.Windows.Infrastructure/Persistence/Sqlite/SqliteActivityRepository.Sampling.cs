using LiveQs.Windows.Core.Activity;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    public async Task RecordSampleAsync(
        ActivitySample sample,
        TimeSpan sampleInterval,
        CancellationToken cancellationToken = default)
    {
        var rule = await GetRuleAsync(sample.AppId, cancellationToken);
        if (rule?.IsExcluded == true)
        {
            await RecordExcludedSampleAsync(sample.CapturedAt.ToUniversalTime(), cancellationToken);
            return;
        }

        var settings = await GetSettingsAsync(cancellationToken);
        var intervalLength = TimeSpan.FromSeconds(Math.Clamp(sampleInterval.TotalSeconds, 1, 300));
        var start = sample.CapturedAt.ToUniversalTime();
        var interval = new SampleInterval(
            sample, start, start + intervalLength, Fingerprint(sample, settings.WindowTitleMode), _timeProvider.GetUtcNow());

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var latest = await ReadLatestIntervalAsync(transaction, cancellationToken);
        if (latest is not null && latest.CanExtend(interval, intervalLength + intervalLength))
        {
            await ExtendIntervalAsync(transaction, latest, interval, cancellationToken);
        }
        else
        {
            await FinalizeOpenIntervalAsync(transaction, interval.RecordedAt, null, cancellationToken);
            await InsertIntervalAsync(transaction, interval, cancellationToken);
        }
        transaction.Commit();
    }

    private static async Task<LatestInterval?> ReadLatestIntervalAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, ended_utc, fingerprint, finalized
            FROM activity_segments ORDER BY id DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LatestInterval(reader.GetInt64(0), ParseTime(reader.GetString(1)), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }

    private static async Task ExtendIntervalAsync(
        SqliteTransaction transaction,
        LatestInterval latest,
        SampleInterval interval,
        CancellationToken cancellationToken)
    {
        var sample = interval.Sample;
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE activity_segments
            SET ended_utc = $ended, last_sample_utc = $sampled,
                is_audio_playing = $audio, is_fullscreen = $fullscreen,
                battery_percent = $battery, battery_charging = $charging,
                sync_version = sync_version + 1, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$ended", UtcText(interval.EndedAt > latest.EndedAt ? interval.EndedAt : latest.EndedAt));
        command.Parameters.AddWithValue("$sampled", UtcText(interval.StartedAt));
        command.Parameters.AddWithValue("$audio", sample.IsAudioPlaying);
        command.Parameters.AddWithValue("$fullscreen", sample.IsFullscreen);
        command.Parameters.AddWithValue("$battery", (object?)sample.BatteryPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("$charging", sample.BatteryCharging is null ? DBNull.Value : sample.BatteryCharging.Value);
        command.Parameters.AddWithValue("$now", UtcText(interval.RecordedAt));
        command.Parameters.AddWithValue("$id", latest.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnqueueIntervalAsync(transaction, latest.Id, interval.RecordedAt, cancellationToken);
    }

    private static async Task InsertIntervalAsync(
        SqliteTransaction transaction,
        SampleInterval interval,
        CancellationToken cancellationToken)
    {
        var sample = interval.Sample;
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO activity_segments(
                started_utc, ended_utc, last_sample_utc, app_id, app_name,
                executable_path, window_title, window_title_hash, is_afk,
                is_audio_playing, is_fullscreen, battery_percent, battery_charging,
                fingerprint, created_utc, updated_utc)
            VALUES(
                $started, $ended, $started, $appId, $appName,
                $path, $title, $titleHash, $afk,
                $audio, $fullscreen, $battery, $charging, $fingerprint, $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", UtcText(interval.StartedAt));
        command.Parameters.AddWithValue("$ended", UtcText(interval.EndedAt));
        command.Parameters.AddWithValue("$appId", sample.AppId);
        command.Parameters.AddWithValue("$appName", sample.AppName);
        command.Parameters.AddWithValue("$path", sample.ExecutablePath);
        command.Parameters.AddWithValue("$title", sample.WindowTitle);
        command.Parameters.AddWithValue("$titleHash", sample.WindowTitleHash);
        command.Parameters.AddWithValue("$afk", sample.IsAfk);
        command.Parameters.AddWithValue("$audio", sample.IsAudioPlaying);
        command.Parameters.AddWithValue("$fullscreen", sample.IsFullscreen);
        command.Parameters.AddWithValue("$battery", (object?)sample.BatteryPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("$charging", sample.BatteryCharging is null ? DBNull.Value : sample.BatteryCharging.Value);
        command.Parameters.AddWithValue("$fingerprint", interval.Fingerprint);
        command.Parameters.AddWithValue("$now", UtcText(interval.RecordedAt));
        var segmentId = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        await EnqueueIntervalAsync(transaction, segmentId, interval.RecordedAt, cancellationToken);
    }

    private async Task RecordExcludedSampleAsync(DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        // Excluding an app interrupts the previous interval without recording the excluded context.
        await FinalizeOpenIntervalAsync(transaction, _timeProvider.GetUtcNow(), capturedAt, cancellationToken);
        // Retain only the sampling instant so excluded context can end recovery without being recorded or uploaded.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO excluded_sampling(captured_utc) VALUES($at) ON CONFLICT DO NOTHING;";
        command.Parameters.AddWithValue("$at", UtcText(capturedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    private static async Task FinalizeOpenIntervalAsync(
        SqliteTransaction transaction,
        DateTimeOffset now,
        DateTimeOffset? endBoundary,
        CancellationToken cancellationToken)
    {
        await using var open = transaction.Connection!.CreateCommand();
        open.Transaction = transaction;
        open.CommandText = "SELECT id FROM activity_segments WHERE finalized = 0 ORDER BY id DESC LIMIT 1;";
        if (await open.ExecuteScalarAsync(cancellationToken) is not long segmentId)
        {
            return;
        }

        await using var finalize = transaction.Connection.CreateCommand();
        finalize.Transaction = transaction;
        finalize.CommandText = """
            UPDATE activity_segments
            SET ended_utc = MIN(ended_utc, MAX(started_utc, COALESCE($boundary, ended_utc))),
                finalized = 1, sync_version = sync_version + 1, updated_utc = $now
            WHERE id = $id;
            """;
        finalize.Parameters.AddWithValue("$id", segmentId);
        finalize.Parameters.AddWithValue("$now", UtcText(now));
        // The previous sample estimates the next interval; excluded activity supersedes that estimate.
        finalize.Parameters.AddWithValue("$boundary", endBoundary is { } boundary ? UtcText(boundary) : DBNull.Value);
        await finalize.ExecuteNonQueryAsync(cancellationToken);
        await EnqueueIntervalAsync(transaction, segmentId, now, cancellationToken);
    }

    private static async Task EnqueueIntervalAsync(
        SqliteTransaction transaction,
        long segmentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        // Updating a payload must preserve its transport backoff and permanent-failure state.
        command.CommandText = """
            INSERT INTO sync_queue(segment_id, next_attempt_utc, updated_utc)
            VALUES($id, $now, $now)
            ON CONFLICT(segment_id) DO UPDATE SET updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", segmentId);
        command.Parameters.AddWithValue("$now", UtcText(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record SampleInterval(
        ActivitySample Sample,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        string Fingerprint,
        DateTimeOffset RecordedAt);

    private sealed record LatestInterval(long Id, DateTimeOffset EndedAt, string Fingerprint, bool Finalized)
    {
        public bool CanExtend(SampleInterval sample, TimeSpan maximumGap) =>
            !Finalized && Fingerprint == sample.Fingerprint && sample.StartedAt <= EndedAt + maximumGap;
    }
}
