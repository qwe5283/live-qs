using System.Globalization;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
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
}
