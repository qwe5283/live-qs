using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
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
    /// <summary>Stable identity of the local database, used to derive event identifiers that never collide with a wiped store.</summary>
    Task<string> GetInstallIdAsync(CancellationToken cancellationToken = default);
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
/// and executable.
/// </summary>
public interface IClassificationRuleSync
{
    Task<ClassificationRuleSet?> RefreshAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISyncClient
{
    /// <summary>Uploads one batch and returns exactly one outcome per item; transport-level failure throws.</summary>
    Task<IReadOnlyList<SyncOutcome>> UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken);
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
