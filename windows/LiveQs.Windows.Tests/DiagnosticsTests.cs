using System.Net;
using System.Net.Http;
using System.Text;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

/// <summary>Captures pushed snapshots so worker tests can assert the diagnostics state machine.</summary>
internal sealed class StubDiagnosticsClient : IDiagnosticsClient
{
    public List<SyncDiagnosticsSnapshot> Snapshots { get; } = [];

    public Task PushAsync(SyncDiagnosticsSnapshot snapshot, AppSettings settings, CancellationToken cancellationToken = default)
    {
        Snapshots.Add(snapshot);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sync-diagnostics state machine: every terminal pass (success, offline
/// backoff, partial ack with permanent rejection, stale revision) ends with a
/// snapshot built from local storage, and the snapshot survives process
/// restart because nothing diagnostic lives in memory.
/// </summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly TestPaths _paths = new();

    // ---------- error describer ----------

    [Fact]
    public void Describer_MapsTransportFailuresToStableCodesAndSafeSummaries()
    {
        var server = SyncErrorDescriber.Describe(new HttpRequestException("ignored", null, HttpStatusCode.BadGateway));
        Assert.Equal("server_error", server.Code);
        Assert.Contains("502", server.Message, StringComparison.Ordinal);

        var rejected = SyncErrorDescriber.Describe(new HttpRequestException("ignored", null, HttpStatusCode.Forbidden));
        Assert.Equal("server_rejected", rejected.Code);

        var network = SyncErrorDescriber.Describe(
            new HttpRequestException("机密文档 C:\\Users\\secret title leaked in exception text"));
        Assert.Equal("network_error", network.Code);
        Assert.DoesNotContain("机密文档", network.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", network.Message, StringComparison.Ordinal);

        Assert.Equal("request_timeout", SyncErrorDescriber.Describe(new TaskCanceledException()).Code);
        Assert.Equal("invalid_sync_response", SyncErrorDescriber.Describe(new InvalidOperationException()).Code);
        Assert.Equal("sync_failed", SyncErrorDescriber.Describe(new Exception()).Code);
    }

    // ---------- repository ring buffer and overview ----------

    [Fact]
    public async Task ErrorRingBufferKeepsNewestTwentyEntriesNewestFirst()
    {
        var repository = await CreateRepositoryAsync();
        for (var index = 0; index < 25; index++)
        {
            await repository.RecordSyncErrorAsync("network_error", $"e{index}", DateTimeOffset.UtcNow.AddSeconds(index));
        }

        var recent = await repository.GetRecentSyncErrorsAsync(10);
        Assert.Equal(10, recent.Count);
        Assert.Equal("e24", recent[0].Message); // newest first
        Assert.Equal("e15", recent[^1].Message);
        Assert.All(recent, entry => Assert.Equal("network_error", entry.Code));

        var all = await repository.GetRecentSyncErrorsAsync(20);
        Assert.Equal(20, all.Count); // bounded ring buffer, older entries pruned
    }

    [Fact]
    public async Task OverviewReportsPendingPermanentOldestAndCollectionFromStorage()
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));

        // A freshly recorded sample is immediately in the outbox, waiting for
        // its first upload attempt.
        var empty = await repository.GetSyncOverviewAsync();
        Assert.Equal(1, empty.PendingCount);
        Assert.Equal(0, empty.PermanentFailureCount);
        Assert.NotNull(empty.OldestPendingAt);
        Assert.NotNull(empty.LastCollectionAt);
        Assert.Null(empty.LastSuccessfulUploadAt);

        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        await repository.MarkSyncFailedAsync([item.SegmentId], "connection refused", DateTimeOffset.UtcNow.AddHours(1));
        await repository.MarkPermanentAsync([item.SegmentId], "invalid_event: bad", DateTimeOffset.UtcNow);
        // A permanent dead letter counts separately from the retryable outbox.
        var overview = await repository.GetSyncOverviewAsync();
        Assert.Equal(0, overview.PendingCount);
        Assert.Equal(1, overview.PermanentFailureCount);
        Assert.Equal(item.StartedAt, overview.OldestPendingAt);

        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow.AddMinutes(2), appId: "other.exe"), TimeSpan.FromSeconds(10));
        var second = (await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(3))).Single(entry => entry.SegmentId != item.SegmentId);
        await repository.MarkSyncedAsync([second.SegmentId], DateTimeOffset.UtcNow);
        var afterAck = await repository.GetSyncOverviewAsync();
        Assert.Equal(0, afterAck.PendingCount);
        Assert.Equal(1, afterAck.PermanentFailureCount);
        Assert.NotNull(afterAck.LastSuccessfulUploadAt);
    }

    // ---------- worker snapshots per state-machine transition ----------

    [Fact]
    public async Task Worker_SuccessPassPushesDrainedSnapshotWithPersistedLastUpload()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        var diagnostics = new StubDiagnosticsClient();
        var worker = CreateWorker(repository, new StubSyncClient { Outcomes = [new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null)] }, diagnostics);

        await worker.RunOnceAsync(CancellationToken.None);

        var snapshot = Assert.Single(diagnostics.Snapshots);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(0, snapshot.PermanentFailureCount);
        Assert.NotNull(snapshot.LastSuccessfulUploadAt);
        Assert.NotNull(snapshot.CollectedAt);
        Assert.Empty(snapshot.RecentErrors);
    }

    [Fact]
    public async Task Worker_OfflinePassPushesPendingQueueWithSafeTransientError()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var diagnostics = new StubDiagnosticsClient();
        var worker = CreateWorker(repository, new StubSyncClient { Failure = new HttpRequestException("connection refused") }, diagnostics);

        await worker.RunOnceAsync(CancellationToken.None);

        var snapshot = Assert.Single(diagnostics.Snapshots);
        Assert.Equal(1, snapshot.PendingCount);
        Assert.NotNull(snapshot.OldestPendingAt);
        var error = Assert.Single(snapshot.RecentErrors);
        Assert.Equal("network_error", error.Code);
        Assert.DoesNotContain("connection refused", error.Message, StringComparison.Ordinal);
        // The queue is scheduled for a later backoff attempt, not retried immediately.
        Assert.Empty(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task Worker_PermanentRejectionEntersFailureCountsAndHistoryWithoutRetrying()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var diagnostics = new StubDiagnosticsClient();
        var client = new StubSyncClient();
        var worker = CreateWorker(repository, client, diagnostics);

        // First pass: one accepted (partial success) and one permanently rejected.
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow.AddMinutes(1), appId: "other.exe"), TimeSpan.FromSeconds(10));
        var items = await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(2));
        var rejection = items[0];
        client.Outcomes =
        [
            new SyncOutcome(items[0], SyncOutcomeKind.Acknowledged, null),
            new SyncOutcome(items[1], SyncOutcomeKind.Rejected, "invalid_event: duration must match the interval bounds.", EventAcknowledgementStatus.Rejected, "invalid_event"),
        ];
        await worker.RunOnceAsync(CancellationToken.None);

        var snapshot = diagnostics.Snapshots[^1];
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(1, snapshot.PermanentFailureCount);
        var error = Assert.Single(snapshot.RecentErrors);
        Assert.Equal("invalid_event", error.Code);
        Assert.Equal("duration must match the interval bounds.", error.Message);

        // Second pass: nothing is retried; the dead letter stays visible and
        // the push still happens so the Owner watches the queue drain.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, diagnostics.Snapshots.Count);
        Assert.Equal(1, diagnostics.Snapshots[1].PermanentFailureCount);
        Assert.Equal(1, client.CallCount); // the idle pass did not call the upload client
    }

    [Fact]
    public async Task Worker_StaleRevisionIsAcknowledgedAndDrainsTheQueue()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        var diagnostics = new StubDiagnosticsClient();
        var worker = CreateWorker(
            repository,
            new StubSyncClient { Outcomes = [new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null, EventAcknowledgementStatus.StaleRevision)] },
            diagnostics);

        await worker.RunOnceAsync(CancellationToken.None);

        var snapshot = Assert.Single(diagnostics.Snapshots);
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Empty(snapshot.RecentErrors);
        Assert.NotNull(snapshot.LastSuccessfulUploadAt);
    }

    [Fact]
    public async Task Worker_SnapshotStateStaysConsistentAcrossProcessRestart()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var beforeRestart = new StubDiagnosticsClient();
        var worker = CreateWorker(repository, new StubSyncClient { Failure = new HttpRequestException("offline") }, beforeRestart);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, beforeRestart.Snapshots[^1].PendingCount);

        // A fresh worker over the same local store (process restart) rebuilds
        // the same diagnostics: the unacknowledged outbox entry and the error
        // history were persisted, nothing diagnostic lived in memory.
        var restarted = new SqliteActivityRepository(_paths, TimeProvider.System);
        await restarted.InitializeAsync();
        await restarted.SaveSettingsAsync(CloudSettings());
        var afterRestart = new StubDiagnosticsClient();
        var worker2 = CreateWorker(restarted, new StubSyncClient(), afterRestart);

        // The entry is still inside its backoff window, so this pass is idle —
        // yet the snapshot must report the same queue the pre-restart worker saw.
        await worker2.RunOnceAsync(CancellationToken.None);

        var snapshot = Assert.Single(afterRestart.Snapshots);
        Assert.Equal(1, snapshot.PendingCount);
        Assert.NotNull(snapshot.OldestPendingAt);
        var error = Assert.Single(snapshot.RecentErrors);
        Assert.Equal("network_error", error.Code); // pre-restart failure history still visible

        // Once the backoff elapses (the clock advances past it), the restarted
        // worker drains the queue normally.
        var item = Assert.Single(await restarted.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddHours(2)));
        var worker3 = CreateWorker(
            restarted,
            new StubSyncClient { Outcomes = [new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null)] },
            afterRestart,
            timeProvider: new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(2)));
        await worker3.RunOnceAsync(CancellationToken.None);

        var drained = afterRestart.Snapshots[^1];
        Assert.Equal(0, drained.PendingCount);
        Assert.NotNull(drained.LastSuccessfulUploadAt);
    }

    // ---------- wire shape ----------

    [Fact]
    public async Task Client_PostsSnapshotWithoutSensitiveContent()
    {
        var repository = await CreateRepositoryAsync();
        var settings = CloudSettings();
        var handler = new RecordingHandler();
        var client = new DiagnosticsClient(new SingleClientFactory(handler));

        await client.PushAsync(
            new SyncDiagnosticsSnapshot(
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2, 1,
                [new SyncErrorEntry("invalid_event", "duration must match the interval bounds.", DateTimeOffset.UtcNow)]),
            settings,
            CancellationToken.None);

        Assert.EndsWith("/api/v1/diagnostics/sync", handler.Request!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer lqdev_test_token", handler.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("window_title", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("机密文档", handler.Body, StringComparison.Ordinal);
        var report = System.Text.Json.JsonSerializer.Deserialize<SyncDiagnosticsReport>(
            handler.Body!, ContractJson.Options);
        Assert.NotNull(report);
        Assert.Equal(2, report!.PendingCount);
        Assert.Equal(1, report.PermanentFailureCount);
        Assert.Equal("invalid_event", report.RecentErrors[0].Code);
    }

    private static SyncWorker CreateWorker(
        SqliteActivityRepository repository,
        StubSyncClient client,
        StubDiagnosticsClient diagnostics,
        TimeProvider? timeProvider = null) =>
        new(repository, repository, client, new SyncStatusService(), new NoopRuleSync(repository), diagnostics, timeProvider ?? TimeProvider.System, NullLogger<SyncWorker>.Instance);

    /// <summary>Clock pinned to a fixed instant so tests can jump past a backoff window.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Refresh stub: the worker's rule refresh never interferes with outbox assertions.</summary>
    private sealed class NoopRuleSync(SqliteActivityRepository repository) : IClassificationRuleSync
    {
        public Task<ClassificationRuleSet?> RefreshAsync(AppSettings settings, CancellationToken cancellationToken = default, bool forceRefresh = false) =>
            repository.GetCachedRuleSetAsync(cancellationToken);
    }

    private static AppSettings CloudSettings() => new()
    {
        CloudSyncEnabled = true,
        ServerBaseUrl = "http://127.0.0.1:8787",
        DeviceToken = "lqdev_test_token",
        DeviceId = "device-1",
    };

    private static ActivitySample Sample(DateTimeOffset time, bool afk = false, string appId = "winword.exe") => new(
        time.ToUniversalTime(), appId, "Word", "C:\\Program Files\\winword.exe", "机密文档.docx - Word", "titlehash",
        afk ? 120 : 0, afk, false, false, null, null);

    private async Task<SqliteActivityRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteActivityRepository(_paths, TimeProvider.System);
        await repository.InitializeAsync();
        return repository;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory)) Directory.Delete(_paths.DataDirectory, true);
    }

    private sealed class TestPaths : IAppPaths
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "LiveQs.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }
        public string DatabasePath => Path.Combine(DataDirectory, "test.db");
        public string LogPath => Path.Combine(DataDirectory, "test.log");
    }
}
