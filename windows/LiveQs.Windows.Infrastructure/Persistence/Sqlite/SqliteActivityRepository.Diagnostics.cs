using System.Globalization;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Sync-diagnostics storage: the bounded recent-error ring buffer and the
/// queue-state overview the sync worker reports to the service. Everything
/// here survives process restarts, so a fresh worker reports the same
/// diagnostics the previous one left behind.
/// </summary>
public sealed partial class SqliteActivityRepository
{
    /// <summary>Recent errors kept for diagnostics; older entries are pruned on insert.</summary>
    internal const int MaxSyncErrorEntries = 20;

    public async Task RecordSyncErrorAsync(string code, string message, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sync_errors(occurred_utc, code, message) VALUES ($occurred, $code, $message);
                DELETE FROM sync_errors WHERE id NOT IN (SELECT id FROM sync_errors ORDER BY id DESC LIMIT $limit);
                """;
            insert.Parameters.AddWithValue("$occurred", UtcText(occurredAt));
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$message", message.Length > 300 ? message[..300] : message);
            insert.Parameters.AddWithValue("$limit", MaxSyncErrorEntries);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<SyncErrorEntry>> GetRecentSyncErrorsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_utc, code, message FROM sync_errors
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 0, MaxSyncErrorEntries));
        var result = new List<SyncErrorEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncErrorEntry(reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(0))));
        }
        return result;
    }

    /// <summary>
    /// One read of the facts a diagnostics snapshot needs. Pending excludes
    /// permanent dead letters (they are counted separately so the Owner can
    /// tell "retrying" from "never retried"); the oldest pending instant and
    /// the last collection instant describe how long data has been waiting
    /// and how fresh local collection is.
    /// </summary>
    public async Task<SyncQueueOverview> GetSyncOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var queue = connection.CreateCommand();
        queue.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN permanent = 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN permanent = 1 THEN 1 ELSE 0 END), 0),
                MIN(s.started_utc)
            FROM sync_queue q JOIN activity_segments s ON s.id = q.segment_id;
            """;
        await using var reader = await queue.ExecuteReaderAsync(cancellationToken);
        var pending = 0;
        var permanent = 0;
        DateTimeOffset? oldestPendingAt = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            pending = Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
            permanent = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
            oldestPendingAt = reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2));
        }

        await using var collection = connection.CreateCommand();
        collection.CommandText = "SELECT MAX(last_sample_utc) FROM activity_segments;";
        var lastCollection = (await collection.ExecuteScalarAsync(cancellationToken)) as string;

        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT last_success_utc FROM sync_state WHERE id = 1;";
        var lastSuccess = (await state.ExecuteScalarAsync(cancellationToken)) as string;

        return new SyncQueueOverview(
            pending,
            permanent,
            oldestPendingAt,
            lastCollection is null ? null : ParseTime(lastCollection),
            lastSuccess is null ? null : ParseTime(lastSuccess));
    }
}
