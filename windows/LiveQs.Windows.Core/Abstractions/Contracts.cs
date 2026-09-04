using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Core.Abstractions;

public interface IForegroundSampler
{
    ActivitySample? Capture(AppSettings settings);
}

/// <summary>
/// The device current state asserted by one heartbeat: an ephemeral, expiring
/// projection, never a historical event and never part of the events outbox.
/// </summary>
public sealed record HeartbeatState(
    DateTimeOffset CapturedAt,
    string ApplicationId,
    string ApplicationLabel,
    bool IsAfk);

public interface IHeartbeatClient
{
    /// <summary>Uploads one heartbeat; a transport-level failure throws and the next cadence retries.</summary>
    Task SendAsync(HeartbeatState state, AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pushes the device's synchronization-state snapshot to the service on the
/// sync cadence. Snapshots carry counts, timestamps, and stable-code errors
/// only; a push failure never breaks the sync loop — the next cadence retries.
/// </summary>
public interface IDiagnosticsClient
{
    Task PushAsync(SyncDiagnosticsSnapshot snapshot, AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IActivityWriter
{
    Task RecordSampleAsync(ActivitySample sample, TimeSpan sampleInterval, CancellationToken cancellationToken = default);
}

public interface IActivityQueryService
{
    Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default);
    Task<TimelinePage> GetTimelinePageAsync(
        DateRange range,
        int pageSize,
        TimelineCursor? cursor = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task SaveApplicationRuleAsync(ApplicationRule rule, CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISyncQueueStore
{
    Task<IReadOnlyList<SyncQueueItem>> GetPendingSyncAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task MarkSyncedAsync(IEnumerable<long> segmentIds, DateTimeOffset syncedAt, CancellationToken cancellationToken = default);
    Task MarkSyncFailedAsync(IEnumerable<long> segmentIds, string error, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default);
    Task MarkPermanentAsync(IEnumerable<long> segmentIds, string error, DateTimeOffset at, CancellationToken cancellationToken = default);
    Task<int> GetPendingSyncCountAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Queue-state overview for sync diagnostics: retryable outbox depth,
    /// permanent failure count, the capture instant of the oldest waiting
    /// observation, the most recent local collection instant, and the most
    /// recent acknowledged upload.
    /// </summary>
    Task<Sync.SyncQueueOverview> GetSyncOverviewAsync(CancellationToken cancellationToken = default);
    /// <summary>Appends one stable-code sync error to the local diagnostics ring buffer (newest kept, bounded).</summary>
    Task RecordSyncErrorAsync(string code, string message, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    /// <summary>Reads the most recent sync errors, newest first.</summary>
    Task<IReadOnlyList<Sync.SyncErrorEntry>> GetRecentSyncErrorsAsync(int limit, CancellationToken cancellationToken = default);
    /// <summary>Stable identity of the local database, used to derive event identifiers that never collide with a wiped store.</summary>
    Task<string> GetInstallIdAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Records the classification outcome the server now holds for a segment
    /// after an acknowledged upload, so explicit reclassification passes can
    /// detect unchanged events instead of burning no-op revisions.
    /// </summary>
    Task RecordUploadOutcomeAsync(long segmentId, ClassificationOutcome? outcome, CancellationToken cancellationToken = default);
    /// <summary>
    /// Persists an acknowledged reclassification: the segment's local revision
    /// advances to the uploaded one and its recorded upload outcome is replaced.
    /// </summary>
    Task RecordReclassifiedAsync(long segmentId, int revision, ClassificationOutcome? outcome, CancellationToken cancellationToken = default);
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

public interface IActivityMaintenance
{
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> DeleteRangeAsync(DateRange range, CancellationToken cancellationToken = default);
    Task ExportCsvAsync(string path, DateRange range, CancellationToken cancellationToken = default);
    Task OptimizeAsync(CancellationToken cancellationToken = default);
}

public interface IStartupManager
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

/// <summary>
/// Local persistence of the Owner's classification rule set: the cache of the
/// last successfully fetched version survives outages so classification keeps
/// working offline, plus the per-installation secret that keys opaque
/// identifiers of locally discovered project names (the secret never leaves
/// the device).
/// </summary>
public interface IClassificationRuleStore
{
    Task<ClassificationRuleSet?> GetCachedRuleSetAsync(CancellationToken cancellationToken = default);
    Task SaveCachedRuleSetAsync(ClassificationRuleSet ruleSet, DateTimeOffset fetchedAt, CancellationToken cancellationToken = default);
    Task<string> GetClassificationSecretAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Refreshes the cached rule set from the service at a bounded cadence. A
/// failed refresh is never fatal: the last successful version stays cached
/// and executable. A reclassification pass forces a refresh when its cached
/// version is older than the task's target version.
/// </summary>
public interface IClassificationRuleSync
{
    Task<ClassificationRuleSet?> RefreshAsync(AppSettings settings, CancellationToken cancellationToken = default, bool forceRefresh = false);
}

public interface ISyncClient
{
    /// <summary>Uploads one batch and returns exactly one outcome per item; transport-level failure throws.</summary>
    Task<IReadOnlyList<SyncOutcome>> UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads pre-computed reclassification decisions (same event identity,
    /// bumped revision, re-interpreted classification) and records the local
    /// consequence of each acknowledgement on its segment; returns exactly one
    /// outcome per decision. Transport-level failure throws.
    /// </summary>
    Task<IReadOnlyList<SyncOutcome>> UploadReclassificationAsync(IReadOnlyList<Reclassification.ReclassificationDecision> decisions, AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Device-side surface of explicit historical reclassification: polling for
/// an open task and reporting outcome counts after a pass. The raw context
/// never leaves the device; only counts and already-derived revisions travel.
/// </summary>
public interface IReclassificationClient
{
    /// <summary>The open task this device should process, or null when there is none or it was already reported.</summary>
    Task<Reclassification.ReclassificationAssignment?> GetAssignmentAsync(AppSettings settings, CancellationToken cancellationToken = default);
    /// <summary>Reports the outcome counts of a completed pass; a transport-level failure throws and the next pass re-reports.</summary>
    Task ReportAsync(Guid taskId, Reclassification.ReclassificationReport report, AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISyncStatusService
{
    SyncStatus Current { get; }
    event EventHandler<SyncStatus>? Changed;
    void Update(SyncStatus status);
}

public interface IAppPaths
{
    string DataDirectory { get; }
    string DatabasePath { get; }
    string LogPath { get; }
}
