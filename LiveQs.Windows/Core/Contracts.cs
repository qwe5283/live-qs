namespace LiveQs.Windows.Core;

public interface IForegroundSampler
{
    ActivitySample? Capture(AppSettings settings);
}

public interface IActivityRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RecordSampleAsync(ActivitySample sample, TimeSpan sampleInterval, CancellationToken cancellationToken = default);
    Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(DateRange range, CancellationToken cancellationToken = default);
    Task<TimelinePage> GetTimelinePageAsync(
        DateRange range,
        int pageSize,
        TimelineCursor? cursor = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default);
    Task SaveApplicationRuleAsync(ApplicationRule rule, CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncQueueItem>> GetPendingSyncAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task MarkSyncedAsync(IEnumerable<long> segmentIds, DateTimeOffset syncedAt, CancellationToken cancellationToken = default);
    Task MarkSyncFailedAsync(IEnumerable<long> segmentIds, string error, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default);
    Task<int> GetPendingSyncCountAsync(CancellationToken cancellationToken = default);
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

public interface ISyncClient
{
    Task UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken);
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
