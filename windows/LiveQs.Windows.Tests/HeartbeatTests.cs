using System.Net;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

/// <summary>
/// Heartbeats are ephemeral current-state projections: the client must send
/// the contract shape with the Device Token and no raw context, and the
/// worker must ride the existing lifecycle gates without ever queueing.
/// </summary>
public sealed class HeartbeatTests
{
    [Fact]
    public async Task Client_PostsContractHeartbeatWithoutRawContext()
    {
        var handler = new RecordingHandler { ResponseFactory = _ => "{}" };
        var client = new HeartbeatClient(new SingleClientFactory(handler));
        var settings = new AppSettings
        {
            CloudSyncEnabled = true,
            ServerBaseUrl = "http://127.0.0.1:8787/",
            DeviceToken = "lqdev_test_token",
            DeviceId = "device-1",
        };
        var capturedAt = DateTimeOffset.Parse("2026-01-15T10:00:00.000+00:00");

        await client.SendAsync(new HeartbeatState(capturedAt, "winword.exe", "Word", IsAfk: false), settings);

        Assert.EndsWith("/api/v1/heartbeats", handler.Request!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer lqdev_test_token", handler.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("机密文档", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("window_title", handler.Body, StringComparison.Ordinal);

        var heartbeat = System.Text.Json.JsonSerializer.Deserialize<LiveQs.Windows.Core.Contracts.HeartbeatRequest>(
            handler.Body!, LiveQs.Windows.Core.Contracts.ContractJson.Options);
        Assert.Equal(LiveQs.Windows.Core.Contracts.Platform.Windows, heartbeat!.Platform);
        Assert.Equal("2026-01-15T10:00:00.000Z", heartbeat.CapturedAt);
        Assert.Equal("winword.exe", heartbeat.Activity.ApplicationId);
        Assert.Equal("Word", heartbeat.Activity.ApplicationLabel);
        Assert.False(heartbeat.Activity.IsAfk);
    }

    [Fact]
    public async Task Client_ThrowsOnNonSuccessSoTheNextCadenceRetries()
    {
        var client = new HeartbeatClient(new BadRequestClientFactory());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(
            new HeartbeatState(DateTimeOffset.UtcNow, "app.exe", string.Empty, true),
            new AppSettings { CloudSyncEnabled = true, ServerBaseUrl = "http://127.0.0.1:8787", DeviceToken = "lqdev_t" }));
    }

    [Fact]
    public async Task Worker_SendsOneHeartbeatPerPassFromTheForegroundSample()
    {
        var sample = new ActivitySample(
            DateTimeOffset.Parse("2026-01-15T10:00:00.000Z"), "winword.exe", "Word",
            @"C:\Program Files\winword.exe", "机密文档.docx - Word", "titlehash", 0, false, false, false, null, null);
        var sampler = new StubSampler(sample);
        var client = new StubHeartbeatClient();
        var worker = new HeartbeatWorker(
            sampler, new StubSettingsStore(Settings(active: true, paused: false, token: "lqdev_test_token")),
            client, TimeProvider.System, NullLogger<HeartbeatWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var state = Assert.Single(client.States);
        Assert.Equal("winword.exe", state.ApplicationId);
        Assert.Equal("Word", state.ApplicationLabel);
        Assert.False(state.IsAfk);
        Assert.Equal(sample.CapturedAt, state.CapturedAt); // capture time is the observation instant
    }

    [Fact]
    public async Task Worker_SkipsHeartbeatsWhenSyncIsDisabledPausedOrTokenless()
    {
        var client = new StubHeartbeatClient();
        var sampler = new StubSampler(ActiveSample());

        foreach (var settings in new[]
                 {
                     Settings(active: false, paused: false, token: "lqdev_test_token"),
                     Settings(active: true, paused: true, token: "lqdev_test_token"),
                     Settings(active: true, paused: false, token: ""),
                 })
        {
            var worker = new HeartbeatWorker(
                sampler, new StubSettingsStore(settings), client, TimeProvider.System, NullLogger<HeartbeatWorker>.Instance);
            await worker.RunOnceAsync(CancellationToken.None);
        }

        Assert.Empty(client.States);
    }

    [Fact]
    public async Task Worker_KeepsRunningAfterATransportFailure()
    {
        var client = new FailingHeartbeatClient();
        var worker = new HeartbeatWorker(
            new StubSampler(ActiveSample()),
            new StubSettingsStore(Settings(active: true, paused: false, token: "lqdev_test_token")),
            client, TimeProvider.System, NullLogger<HeartbeatWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None); // must not throw; the next cadence retries

        Assert.Equal(1, client.CallCount);
    }

    private static ActivitySample ActiveSample() => new(
        DateTimeOffset.Parse("2026-01-15T10:00:00.000Z"), "devenv.exe", "Visual Studio",
        @"C:\Program Files\Microsoft Visual Studio\devenv.exe", "机密项目 - Visual Studio", "titlehash", 0, false, false, false, null, null);

    private static AppSettings Settings(bool active, bool paused, string token) => new()
    {
        CloudSyncEnabled = active,
        ServerBaseUrl = "http://127.0.0.1:8787",
        DeviceToken = token,
        DeviceId = "device-1",
        SamplingPaused = paused,
    };

    private sealed class StubSampler(ActivitySample? sample) : IForegroundSampler
    {
        public ActivitySample? Capture(AppSettings settings) => sample;
    }

    private sealed class StubSettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task SaveApplicationRuleAsync(ApplicationRule rule, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHeartbeatClient : IHeartbeatClient
    {
        public List<HeartbeatState> States { get; } = new();

        public Task SendAsync(HeartbeatState state, AppSettings settings, CancellationToken cancellationToken = default)
        {
            States.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingHeartbeatClient : IHeartbeatClient
    {
        public int CallCount { get; private set; }

        public Task SendAsync(HeartbeatState state, AppSettings settings, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new HttpRequestException("connection refused");
        }
    }

    private sealed class BadRequestClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new BadRequestHandler()) { BaseAddress = new Uri("http://127.0.0.1:8787/") };

        private sealed class BadRequestHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"code":"invalid_request","message":"bad"}}"""),
                });
        }
    }
}
