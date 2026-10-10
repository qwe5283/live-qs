using LiveQs.Windows.Core.Common;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    private static async Task RecordDeviceStateDeletionAsync(
        SqliteTransaction transaction, DateRange range, CancellationToken cancellationToken)
    {
        if (range.End <= range.Start)
        {
            return;
        }

        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MIN(started_utc), MAX(ended_utc) FROM device_state_deleted_ranges
            WHERE started_utc <= $end AND ended_utc >= $start;
            """;
        command.Parameters.AddWithValue("$start", UtcText(range.Start));
        command.Parameters.AddWithValue("$end", UtcText(range.End));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
            {
                var start = ParseTime(reader.GetString(0));
                var end = ParseTime(reader.GetString(1));
                range = new DateRange(start < range.Start ? start : range.Start, end > range.End ? end : range.End);
            }
        }

        // Coalesce adjacent ranges, including the retention floor, without ever reopening deleted history.
        command.Parameters["$start"].Value = UtcText(range.Start);
        command.Parameters["$end"].Value = UtcText(range.End);
        command.CommandText = """
            DELETE FROM device_state_deleted_ranges WHERE started_utc <= $end AND ended_utc >= $start;
            INSERT INTO device_state_deleted_ranges(started_utc, ended_utc) VALUES($start, $end);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
