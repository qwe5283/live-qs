using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    public async Task<DashboardSnapshot> GetDashboardAsync(
        DateRange range,
        CancellationToken cancellationToken = default)
    {
        var segments = await ReadSegmentsAsync(range, cancellationToken);
        var states = await ReadDeviceStateEventsAsync(range, cancellationToken);
        return DashboardStatistics.Calculate(segments, range, TimeZoneInfo.Local, states);
    }

    public async Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(
        DateRange range,
        CancellationToken cancellationToken = default) =>
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
        var states = await ReadDeviceStateEventsAsync(range, cancellationToken);
        var recoveryWindows = await ReadRecoveryWindowsAsync(states, range, cancellationToken);
        var status = await GetDeviceStateImportStatusAsync(cancellationToken);
        return new TimelinePage(pageItems, nextCursor, hasMore)
        {
            StateEvents = states,
            RecoveryWindows = recoveryWindows,
            StateWarning = status.Warning
        };
    }

    private Task<IReadOnlyList<ActivitySegment>> ReadSegmentsAsync(
        DateRange range,
        CancellationToken cancellationToken) =>
        ReadSegmentsPageAsync(range, int.MaxValue, null, cancellationToken);

    private async Task<IReadOnlyList<ActivitySegment>> ReadSegmentsPageAsync(
        DateRange range,
        int take,
        TimelineCursor? cursor,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var cursorPredicate = cursor is null
            ? ""
            : "AND (s.started_utc < $cursorStarted OR (s.started_utc = $cursorStarted AND s.id < $cursorId))";
        // A newer sample before the query start can invalidate an older estimate's overlapping tail.
        // Keep that context in the predicate so a date change cannot resurrect the superseded estimate.
        command.CommandText = $"""
            SELECT s.id, s.started_utc, s.ended_utc, s.app_id,
                   COALESCE(NULLIF(r.alias, ''), s.app_name) AS display_name,
                   s.window_title, s.is_afk, s.is_audio_playing, s.is_fullscreen,
                   COALESCE(NULLIF(r.category, ''), '未分类') AS category, s.executable_path
            FROM activity_segments s
            LEFT JOIN application_rules r ON r.app_id = s.app_id
            WHERE s.started_utc < $end AND s.ended_utc > $start
              AND COALESCE(r.is_excluded, 0) = 0
              AND (s.started_utc >= $start OR NOT EXISTS (
                  SELECT 1 FROM activity_segments newer
                  LEFT JOIN application_rules newer_rule ON newer_rule.app_id = newer.app_id
                  WHERE newer.started_utc <= $start
                    AND (newer.started_utc > s.started_utc
                         OR (newer.started_utc = s.started_utc AND newer.id > s.id))
                    AND newer.ended_utc > newer.started_utc
                    AND COALESCE(newer_rule.is_excluded, 0) = 0
              ))
              {cursorPredicate}
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

        var result = new List<ActivitySegment>(Math.Min(take, 1_000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var appId = reader.GetString(3);
            result.Add(new ActivitySegment(
                reader.GetInt64(0), ParseTime(reader.GetString(1)), ParseTime(reader.GetString(2)), appId,
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7),
                reader.GetBoolean(8), reader.GetString(9), ColorFor(appId))
            {
                ExecutablePath = reader.GetString(10),
            });
        }
        return result;
    }
}
