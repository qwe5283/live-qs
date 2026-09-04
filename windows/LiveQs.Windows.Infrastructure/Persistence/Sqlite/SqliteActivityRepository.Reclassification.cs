using System.Globalization;
using System.Text.Json;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    private static ClassificationOutcome? ParseUploadOutcome(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClassificationOutcome>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A damaged record degrades to "outcome unknown", never to a crash.
            return null;
        }
    }

    private static string? SerializeUploadOutcome(ClassificationOutcome? outcome) =>
        outcome is null ? null : JsonSerializer.Serialize(outcome, JsonOptions);

    /// <summary>
    /// The recorded upload outcome is what the device remembers the server
    /// accepted for this segment's latest revision; explicit reclassification
    /// compares a re-computed outcome against it to leave unchanged events
    /// untouched. When no outcome is recorded, the device is deliberately
    /// conservative: it may add a subject but never strip one.
    /// </summary>
    public async Task RecordUploadOutcomeAsync(long segmentId, ClassificationOutcome? outcome, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE activity_segments SET upload_outcome = $outcome, updated_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$outcome", (object?)SerializeUploadOutcome(outcome) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$id", segmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Persists an acknowledged reclassification: the local revision advances
    /// to the uploaded one so later passes and checkpoint streams never retry
    /// a lower revision, and the recorded outcome matches the new cloud state.
    /// </summary>
    public async Task RecordReclassifiedAsync(long segmentId, int revision, ClassificationOutcome? outcome, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE activity_segments
            SET sync_version = $revision, upload_outcome = $outcome, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$outcome", (object?)SerializeUploadOutcome(outcome) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$id", segmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Candidates for an explicit reclassification pass: finalized, non-AFK
    /// segments the server has already acknowledged (they have no outbox row),
    /// within the optional task range. Still-open checkpoints are excluded —
    /// every extension of theirs re-uploads with the current cached rules, so
    /// the live checkpoint stream already applies new rules to them.
    /// </summary>
    public async Task<IReadOnlyList<SyncQueueItem>> GetReclassificationCandidatesAsync(
        DateTimeOffset? from, DateTimeOffset? to, int limit, long afterSegmentId, CancellationToken cancellationToken = default)
    {
        var bounds = "";
        if (from.HasValue) bounds += " AND s.started_utc >= $from";
        if (to.HasValue) bounds += " AND s.started_utc < $to";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, 0, s.started_utc, s.ended_utc,
                   s.app_id, s.app_name, s.window_title, s.window_title_hash,
                   s.is_afk, s.is_audio_playing, s.is_fullscreen,
                   s.sync_version, s.finalized, s.upload_outcome
            FROM activity_segments s
            WHERE s.finalized = 1 AND s.is_afk = 0
              AND s.id NOT IN (SELECT segment_id FROM sync_queue)
              AND s.id > $after{bounds}
            ORDER BY s.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", afterSegmentId);
        if (from.HasValue) command.Parameters.AddWithValue("$from", UtcText(from.Value));
        if (to.HasValue) command.Parameters.AddWithValue("$to", UtcText(to.Value));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var result = new List<SyncQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncQueueItem(
                reader.GetInt64(0), reader.GetInt32(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
                (int)reader.GetInt64(11), reader.GetBoolean(12), ParseUploadOutcome(reader.IsDBNull(13) ? null : reader.GetString(13))));
        }
        return result;
    }
}
