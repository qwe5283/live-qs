using System.Text.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    public async Task AppendDeviceStateEventsAsync(IReadOnlyList<DeviceStateEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await InsertDeviceStateEventsAsync(transaction, events, cancellationToken);
        transaction.Commit();
    }

    public async Task CompleteDeviceStateImportAsync(IReadOnlyList<DeviceStateEvent> events, DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await InsertDeviceStateEventsAsync(transaction, events, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_state_import(id, imported_until_utc, warning) VALUES(1, $until, '')
            ON CONFLICT(id) DO UPDATE SET imported_until_utc = MAX(COALESCE(imported_until_utc, ''), $until), warning = '';
            """;
        command.Parameters.AddWithValue("$until", UtcText(until));
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<DeviceStateImportStatus> GetDeviceStateImportStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT imported_until_utc, warning FROM device_state_import WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DeviceStateImportStatus(reader.IsDBNull(0) ? null : ParseTime(reader.GetString(0)), reader.GetString(1))
            : new DeviceStateImportStatus(null, "");
    }

    public async Task SetDeviceStateImportWarningAsync(string warning, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_state_import(id, warning) VALUES(1, $warning)
            ON CONFLICT(id) DO UPDATE SET warning = $warning;
            """;
        command.Parameters.AddWithValue("$warning", warning);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDeviceStateEventsAsync(SqliteTransaction transaction,
        IReadOnlyList<DeviceStateEvent> events, CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_state_events(event_key, occurred_utc, json)
            SELECT $key, $at, $json WHERE NOT EXISTS (
                SELECT 1 FROM device_state_deleted_ranges WHERE started_utc <= $at AND ended_utc > $at
            )
            ON CONFLICT(event_key) DO NOTHING;
            """;
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var json = command.Parameters.Add("$json", SqliteType.Text);
        foreach (var value in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key.Value = value.Key;
            at.Value = UtcText(value.OccurredAt);
            json.Value = JsonSerializer.Serialize(value, _jsonOptions);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<DeviceStateContext> ReadDeviceStateContextAsync(DateRange range,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Read evidence and its deletion boundaries from one SQLite snapshot, including cross-day context.
        command.CommandText = """
            SELECT json, NULL, NULL FROM device_state_events WHERE occurred_utc >= $start AND occurred_utc <= $end
            UNION ALL
            SELECT NULL, started_utc, ended_utc FROM device_state_deleted_ranges
            WHERE started_utc < $end AND ended_utc > $start;
            """;
        command.Parameters.AddWithValue("$start", UtcText(range.Start.AddDays(-31)));
        command.Parameters.AddWithValue("$end", UtcText(range.End.AddDays(31)));
        var events = new List<DeviceStateEvent>();
        var deletedRanges = new List<DateRange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                deletedRanges.Add(new DateRange(ParseTime(reader.GetString(1)), ParseTime(reader.GetString(2))));
            }
            else if (JsonSerializer.Deserialize<DeviceStateEvent>(reader.GetString(0), _jsonOptions) is { } value)
            {
                events.Add(value);
            }
        }
        return new DeviceStateContext(events.OrderBy(value => value.OccurredAt).ThenBy(value => value.Key).ToArray(),
            deletedRanges);
    }

    private async Task<IReadOnlyList<DeviceRecoveryWindow>> ReadRecoveryWindowsAsync(
        DeviceStateContext context, DateRange range, CancellationToken cancellationToken)
    {
        var windows = DeviceStateTimeline.CreateRecoveryWindows(context.Events,
                DeviceStateTimeline.Pair(context.Events, context.DeletedRanges), _ => null, _timeProvider.GetUtcNow(),
                context.DeletedRanges)
            .Where(window => window.StartedAt < range.End && window.EndedAt > range.Start).ToArray();
        if (windows.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Sampling context is independent of display exclusions, date clipping and pagination.
        command.CommandText = """
            SELECT MIN(sampled_utc) FROM (
                SELECT MAX(s.started_utc, $at) AS sampled_utc FROM activity_segments s
                WHERE s.ended_utc > s.started_utc AND s.ended_utc > $at AND s.started_utc <= $deadline
                  AND NOT EXISTS (
                      SELECT 1 FROM activity_segments newer
                      WHERE newer.started_utc <= $at
                        AND (newer.started_utc > s.started_utc
                             OR (newer.started_utc = s.started_utc AND newer.id > s.id))
                        AND newer.ended_utc > newer.started_utc
                  )
                UNION ALL
                SELECT captured_utc FROM excluded_sampling WHERE captured_utc >= $at AND captured_utc <= $deadline
            );
            """;
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var deadline = command.Parameters.Add("$deadline", SqliteType.Text);
        var resolved = new List<DeviceRecoveryWindow>();
        foreach (var window in windows)
        {
            at.Value = UtcText(window.StartedAt);
            deadline.Value = UtcText(window.EndedAt);
            var end = await command.ExecuteScalarAsync(cancellationToken) is string sampled
                ? ParseTime(sampled) : window.EndedAt;
            if (end > window.StartedAt)
            {
                resolved.Add(window with
                {
                    EndedAt = end
                });
            }
        }
        return resolved;
    }

    private sealed record DeviceStateContext(IReadOnlyList<DeviceStateEvent> Events, IReadOnlyList<DateRange> DeletedRanges);
}
