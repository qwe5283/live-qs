using System.Net;
using System.Text;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly TestPaths _paths = new();

    [Fact]
    public async Task Client_PostsContractBatchWithoutRawContext()
    {
        var repository = await CreateRepositoryAsync();
        var settings = CloudSettings();
        await repository.SaveSettingsAsync(settings);
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow.AddMinutes(-5)), TimeSpan.FromSeconds(10));
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));

        var handler = new RecordingHandler { ResponseFactory = body => AckResponse(body, "accepted") };
        var client = new CloudSyncClient(new SingleClientFactory(handler), repository, TimeProvider.System);
        var items = await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1));

        var outcomes = await client.UploadAsync(items, settings, CancellationToken.None);

        Assert.EndsWith("/api/v1/events/batch", handler.Request!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer lqdev_test_token", handler.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("机密文档", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Program Files", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("window_title", handler.Body, StringComparison.Ordinal);

        var batch = System.Text.Json.JsonSerializer.Deserialize<LiveQs.Windows.Core.Contracts.EventBatchRequest>(
            handler.Body!, LiveQs.Windows.Core.Contracts.ContractJson.Options);
        var first = batch!.Events[0];
        Assert.Equal("winword.exe", first.Payload.ApplicationId);
        Assert.Equal(2, first.Revision); // the finalized interval carries its final revision
        Assert.Equal(LiveQs.Windows.Core.Contracts.FinalizationState.Final, first.FinalizationState);
        Assert.Equal(10_000, first.Payload.Duration!.Value);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", first.StartAt);
        Assert.All(outcomes, outcome => Assert.Equal(SyncOutcomeKind.Acknowledged, outcome.Kind));
    }

    [Fact]
    public async Task Client_MapsDuplicateStaleAndRejectedAcksPerItem()
    {
        var repository = await CreateRepositoryAsync();
        var settings = CloudSettings();
        await repository.SaveSettingsAsync(settings);
        for (var index = 0; index < 4; index++)
        {
            await repository.RecordSampleAsync(
                Sample(DateTimeOffset.UtcNow.AddMinutes(index), appId: $"app{index}.exe"), TimeSpan.FromSeconds(10));
        }
        var items = await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(4, items.Count);

        var acks = new[] { "accepted", "duplicate", "stale_revision", "rejected" };
        var handler = new RecordingHandler
        {
            ResponseFactory = body =>
            {
                var index = 0;
                var builder = new StringBuilder("""{"results":[""");
                using var document = System.Text.Json.JsonDocument.Parse(body);
                foreach (var _ in document.RootElement.GetProperty("events").EnumerateArray())
                {
                    if (index > 0) builder.Append(',');
                    var status = acks[index];
                    builder.Append(status == "rejected"
                        ? """{"event_id":"00000000-0000-0000-0000-000000000000","revision":1,"status":"rejected","error":{"code":"invalid_event","message":"bad payload"}}"""
                        : $$"""{"event_id":"00000000-0000-0000-0000-000000000000","revision":1,"status":"{{status}}"}""");
                    index++;
                }
                builder.Append("]}");
                return builder.ToString();
            },
        };
        var client = new CloudSyncClient(new SingleClientFactory(handler), repository, TimeProvider.System);

        var outcomes = await client.UploadAsync(items, settings, CancellationToken.None);

        Assert.Equal(
            new[] { SyncOutcomeKind.Acknowledged, SyncOutcomeKind.Acknowledged, SyncOutcomeKind.Acknowledged, SyncOutcomeKind.Rejected },
            outcomes.Select(outcome => outcome.Kind).ToArray());
        Assert.Equal("invalid_event: bad payload", outcomes[3].Error);
    }

    [Fact]
    public async Task Client_ChunksBatchesToTheContractLimit()
    {
        var repository = await CreateRepositoryAsync();
        var settings = CloudSettings();
        await repository.SaveSettingsAsync(settings);
        for (var index = 0; index < 101; index++)
        {
            await repository.RecordSampleAsync(
                Sample(DateTimeOffset.UtcNow.AddMinutes(index), appId: $"app{index}.exe"), TimeSpan.FromSeconds(10));
        }
        var items = await repository.GetPendingSyncAsync(500, DateTimeOffset.UtcNow.AddMinutes(200));
        Assert.Equal(101, items.Count);

        var handler = new RecordingHandler { ResponseFactory = body => AckResponse(body, "accepted") };
        var client = new CloudSyncClient(new SingleClientFactory(handler), repository, TimeProvider.System);

        var outcomes = await client.UploadAsync(items, settings, CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(100, CountEvents(handler.Bodies[0]));
        Assert.Equal(1, CountEvents(handler.Bodies[1]));
        Assert.Equal(101, outcomes.Count);
    }

    [Fact]
    public async Task Worker_RemovesOutboxItemsOnlyAfterTheirRevisionIsAcknowledged()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        var statusService = new SyncStatusService();

        var client = new StubSyncClient { Outcomes = new[] { new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null) } };
        var worker = CreateWorker(repository, client, statusService);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await repository.GetPendingSyncCountAsync());
        Assert.True(statusService.Current.LastSuccessAt is not null);
    }

    [Fact]
    public async Task Worker_NeverRetriesPermanentlyRejectedItems()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        var statusService = new SyncStatusService();

        var client = new StubSyncClient
        {
            Outcomes = new[] { new SyncOutcome(item, SyncOutcomeKind.Rejected, "invalid_event: payload is not valid") },
        };
        var worker = CreateWorker(repository, client, statusService);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Empty(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, client.CallCount); // no pending items remain, so no retry happened
        Assert.Contains("invalid_event", statusService.Current.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_DropsStaleRevisionsBecauseTheServerHoldsNewerFacts()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var item = Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(1)));
        var statusService = new SyncStatusService();

        var client = new StubSyncClient { Outcomes = new[] { new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null) } };
        var worker = CreateWorker(repository, client, statusService);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await repository.GetPendingSyncCountAsync());
    }

    [Fact]
    public async Task Worker_BacksoffTransientFailuresAndRetriesLater()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var statusService = new SyncStatusService();
        var client = new StubSyncClient { Failure = new HttpRequestException("connection refused") };
        var worker = CreateWorker(repository, client, statusService);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Single(await repository.GetPendingSyncAsync(10, DateTimeOffset.UtcNow.AddHours(2)));
    }

    [Fact]
    public async Task Worker_DisabledSyncNeverCallsTheClient()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings() with { CloudSyncEnabled = false });
        await repository.RecordSampleAsync(Sample(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(10));
        var client = new StubSyncClient { Outcomes = Array.Empty<SyncOutcome>() };
        var worker = CreateWorker(repository, client, new SyncStatusService());

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, client.CallCount);
    }

    private static SyncWorker CreateWorker(SqliteActivityRepository repository, StubSyncClient client, SyncStatusService statusService) =>
        new(repository, repository, client, statusService, TimeProvider.System, NullLogger<SyncWorker>.Instance);

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

    private static string AckResponse(string body, string status)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var count = document.RootElement.GetProperty("events").GetArrayLength();
        var builder = new StringBuilder("""{"results":[""");
        for (var index = 0; index < count; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append($$"""{"event_id":"00000000-0000-0000-0000-000000000000","revision":1,"status":"{{status}}"}""");
        }
        builder.Append("]}");
        return builder.ToString();
    }

    private static int CountEvents(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("events").GetArrayLength();
    }

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

    private sealed class StubSyncClient : ISyncClient
    {
        public IReadOnlyList<SyncOutcome>? Outcomes { get; set; }
        public Exception? Failure { get; set; }
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SyncOutcome>> UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken)
        {
            CallCount++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Outcomes ?? (IReadOnlyList<SyncOutcome>)Array.Empty<SyncOutcome>());
        }
    }
}
