using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Core.Abstractions;

public interface ISyncQueueStore
{
    Task<IReadOnlyList<SyncQueueItem>> GetPendingSyncAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task MarkSyncedAsync(
        IEnumerable<SyncRevision> revisions,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken = default);

    Task MarkSyncFailedAsync(
        IEnumerable<long> segmentIds,
        string error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task MarkPermanentAsync(
        IEnumerable<SyncRevision> revisions,
        string error,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    Task<int> GetPendingSyncCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queue-state overview for sync diagnostics: retryable outbox depth,
    /// permanent failure count, the capture instant of the oldest waiting
    /// observation, the most recent local collection instant, and the most
    /// recent acknowledged upload.
    /// </summary>
    Task<SyncQueueOverview> GetSyncOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends one stable-code sync error to the local diagnostics ring buffer (newest kept, bounded).</summary>
    Task RecordSyncErrorAsync(
        string code,
        string message,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the most recent sync errors, newest first.</summary>
    Task<IReadOnlyList<SyncErrorEntry>> GetRecentSyncErrorsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Stable identity of the local database, used to derive event identifiers that never collide with a wiped store.</summary>
    Task<string> GetInstallIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the classification outcome the server now holds for a segment
    /// after an acknowledged upload, so explicit reclassification passes can
    /// detect unchanged events instead of burning no-op revisions.
    /// </summary>
    Task RecordUploadOutcomeAsync(
        SyncRevision revision,
        ClassificationOutcome? outcome,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an acknowledged reclassification: the segment's local revision
    /// advances to the uploaded one and its recorded upload outcome is replaced.
    /// </summary>
    Task RecordReclassifiedAsync(
        long segmentId,
        int revision,
        ClassificationOutcome? outcome,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalized, non-AFK segments already acknowledged by the server (out of
    /// the outbox), ordered by segment identity after <paramref name="afterSegmentId"/>,
    /// within the optional task range. These are the segments explicit
    /// reclassification may re-evaluate; still-open checkpoints stay owned by
    /// the live checkpoint stream.
    /// </summary>
    Task<IReadOnlyList<SyncQueueItem>> GetReclassificationCandidatesAsync(
        DateTimeOffset? from, DateTimeOffset? to, int limit, long afterSegmentId, CancellationToken cancellationToken = default);
}
