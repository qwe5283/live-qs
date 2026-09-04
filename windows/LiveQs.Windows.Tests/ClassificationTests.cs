using System.Text;
using System.Text.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Infrastructure.Classification;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

/// <summary>
/// Golden samples for local semantic classification: rule priority, no-match,
/// conflicts, version output, cross-platform platform filtering, and the
/// opaque device-secret HMAC identifiers of unapproved project names.
/// </summary>
public sealed class ClassificationTests : IDisposable
{
    private const string TestSecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private const string CrossPlatformRuleSetJson = """
        {
          "rule_set_version": 7,
          "updated_at": "2026-08-21T02:00:00.000Z",
          "entities": [
            { "entity_id": "svc.bilibili", "kind": "service", "name": "哔哩哔哩" },
            { "entity_id": "project.liveqs", "kind": "project", "name": "LiveQs" }
          ],
          "rules": [
            {
              "rule_id": "android.bilibili.package",
              "platform": "any",
              "kind": "application",
              "pattern": "tv.danmaku.bili",
              "priority": 0,
              "confidence": 1,
              "subject_entity_id": "svc.bilibili",
              "dynamic": false,
              "version": 1,
              "updated_at": "2026-08-21T02:00:00.000Z"
            },
            {
              "rule_id": "rider.projects.discover",
              "platform": "windows",
              "kind": "title_regex",
              "pattern": "^RIDER-(.+)$",
              "priority": 5,
              "confidence": 0.9,
              "subject_entity_id": null,
              "dynamic": true,
              "version": 2,
              "updated_at": "2026-08-21T02:00:00.000Z"
            },
            {
              "rule_id": "edge.bilibili.title",
              "platform": "windows",
              "kind": "title_keyword",
              "pattern": "bilibili",
              "priority": 10,
              "confidence": 0.8,
              "subject_entity_id": "svc.bilibili",
              "dynamic": false,
              "version": 3,
              "updated_at": "2026-08-21T02:00:00.000Z"
            }
          ]
        }
        """;

    [Fact]
    public void ApplicationRule_MatchesPackageOrExecutableName()
    {
        var ruleSet = RuleSet(CrossPlatformRuleSetJson);
        // The same subject for the Android package (cross-platform mapping golden sample).
        var outcome = ClassificationEngine.Classify(ruleSet, "android", "tv.danmaku.bili", null, TestSecret);
        Assert.NotNull(outcome);
        Assert.Equal("svc.bilibili", outcome.SubjectId);
        Assert.Equal("android.bilibili.package", outcome.RuleId);
        Assert.Equal(1, outcome.RuleVersion);
        Assert.Equal(1, outcome.Confidence);
    }

    [Fact]
    public void ApplicationRule_MatchesExecutableNameCaseInsensitively()
    {
        var ruleSet = RuleSet("""
            { "rule_set_version": 1, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "word.app", "platform": "any", "kind": "application", "pattern": "winword.exe",
                "priority": 0, "confidence": 1, "subject_entity_id": "svc.office", "dynamic": false,
                "version": 4, "updated_at": null } ] }
            """);
        var outcome = ClassificationEngine.Classify(ruleSet, "windows", "WINWORD.EXE", null, TestSecret);
        Assert.NotNull(outcome);
        Assert.Equal(("svc.office", "word.app", 4L, 1.0), (outcome.SubjectId, outcome.RuleId, outcome.RuleVersion, outcome.Confidence));
    }

    [Fact]
    public void TitleKeywordRule_MatchesCaseInsensitivelyAndCitesTheRuleVersion()
    {
        var ruleSet = RuleSet(CrossPlatformRuleSetJson);
        // An Edge session whose local title contains the Bilibili keyword maps
        // to the same service subject as the Android package.
        var outcome = ClassificationEngine.Classify(ruleSet, "windows", "msedge.exe", "哔哩哔哩 (゜-゜)つロ bilibili 哔哩哔哩~ - Edge", TestSecret);
        Assert.NotNull(outcome);
        Assert.Equal("svc.bilibili", outcome.SubjectId);
        Assert.Equal("edge.bilibili.title", outcome.RuleId);
        Assert.Equal(3, outcome.RuleVersion);
        Assert.Equal(0.8, outcome.Confidence);
    }

    [Fact]
    public void NoMatch_ProducesNoSubjectInsteadOfGuessing()
    {
        var ruleSet = RuleSet(CrossPlatformRuleSetJson);
        Assert.Null(ClassificationEngine.Classify(ruleSet, "windows", "msedge.exe", "机密文档 - 内部系统", TestSecret));
        Assert.Null(ClassificationEngine.Classify(ruleSet, "windows", "notepad.exe", null, TestSecret));
        Assert.Null(ClassificationEngine.Classify(null, "windows", "tv.danmaku.bili", "title", TestSecret));
        Assert.Null(ClassificationEngine.Classify(
            RuleSet("""{ "rule_set_version": 0, "updated_at": null, "entities": [], "rules": [] }"""),
            "windows", "tv.danmaku.bili", "bilibili", TestSecret));
    }

    [Fact]
    public void PriorityDecidesConflictsAndEqualPriorityBreaksTiesByRuleId()
    {
        // Both keyword rules match; the higher priority wins.
        var conflicted = RuleSet("""
            { "rule_set_version": 2, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "b.mapping", "platform": "windows", "kind": "title_keyword", "pattern": "bilibili",
                "priority": 1, "confidence": 0.5, "subject_entity_id": "svc.other", "dynamic": false, "version": 1, "updated_at": null },
              { "rule_id": "a.mapping", "platform": "windows", "kind": "title_keyword", "pattern": "bilibili",
                "priority": 10, "confidence": 0.5, "subject_entity_id": "svc.bilibili", "dynamic": false, "version": 1, "updated_at": null } ] }
            """);
        var winner = ClassificationEngine.Classify(conflicted, "windows", "msedge.exe", "bilibili", TestSecret);
        Assert.Equal("svc.bilibili", winner!.SubjectId);
        Assert.Equal("a.mapping", winner.RuleId);

        // Equal priorities resolve deterministically by ascending rule_id,
        // independent of the order the rules arrive in.
        var tie = RuleSet("""
            { "rule_set_version": 2, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "zzz.tie", "platform": "windows", "kind": "title_keyword", "pattern": "rider",
                "priority": 3, "confidence": 0.5, "subject_entity_id": "svc.z", "dynamic": false, "version": 1, "updated_at": null },
              { "rule_id": "aaa.tie", "platform": "windows", "kind": "title_keyword", "pattern": "rider",
                "priority": 3, "confidence": 0.5, "subject_entity_id": "svc.a", "dynamic": false, "version": 1, "updated_at": null } ] }
            """);
        var tieWinner = ClassificationEngine.Classify(tie, "windows", "rider64.exe", "Rider", TestSecret);
        Assert.Equal("svc.a", tieWinner!.SubjectId);
    }

    [Fact]
    public void PlatformScopedRulesDoNotApplyToOtherPlatforms()
    {
        var ruleSet = RuleSet(CrossPlatformRuleSetJson);
        // The windows-only Edge keyword rule cannot classify Android events.
        Assert.Null(ClassificationEngine.Classify(ruleSet, "android", "msedge.exe", "bilibili", TestSecret));
        // The windows-only discovery rule cannot run on Android either.
        Assert.Null(ClassificationEngine.Classify(ruleSet, "android", "com.jetbrains.rider", "RIDER-liveqs", TestSecret));
    }

    [Fact]
    public void ApprovedProjectRule_MapsByAliasWithoutUploadingTheRawTitle()
    {
        var ruleSet = RuleSet("""
            { "rule_set_version": 3, "updated_at": null, "entities": [
                { "entity_id": "project.liveqs", "kind": "project", "name": "LiveQs" } ],
              "rules": [
                { "rule_id": "rider.liveqs.title", "platform": "windows", "kind": "title_keyword", "pattern": "liveqs",
                  "priority": 20, "confidence": 0.9, "subject_entity_id": "project.liveqs", "dynamic": false,
                  "version": 2, "updated_at": null } ] }
            """);
        var outcome = ClassificationEngine.Classify(ruleSet, "windows", "rider64.exe", "D:\\code\\LiveQs - Rider 2026", TestSecret);
        Assert.NotNull(outcome);
        Assert.Equal("project.liveqs", outcome.SubjectId);
    }

    [Fact]
    public void DynamicProjectRule_ReportsOpaqueDeviceSecretHmacIdentifiers()
    {
        var ruleSet = RuleSet(CrossPlatformRuleSetJson);

        var outcome = ClassificationEngine.Classify(ruleSet, "windows", "rider64.exe", "RIDER-LiveQs", TestSecret);
        Assert.NotNull(outcome);
        Assert.Equal("rider.projects.discover", outcome.RuleId);
        Assert.Equal(2, outcome.RuleVersion);
        Assert.StartsWith("unapproved-", outcome.SubjectId, StringComparison.Ordinal);
        Assert.Equal(32, outcome.SubjectId["unapproved-".Length..].Length); // 128-bit HMAC prefix, hex

        // Stable for the same name (aggregation), different per name, and
        // keyed by the device secret — never a plain unsalted digest.
        var again = ClassificationEngine.Classify(ruleSet, "windows", "rider64.exe", "RIDER-LiveQs", TestSecret);
        Assert.Equal(outcome.SubjectId, again!.SubjectId);
        var other = ClassificationEngine.Classify(ruleSet, "windows", "rider64.exe", "RIDER-AnotherProject", TestSecret);
        Assert.NotEqual(outcome.SubjectId, other!.SubjectId);
        var otherSecret = ClassificationEngine.Classify(ruleSet, "windows", "rider64.exe", "RIDER-LiveQs", "c3dhZi1kZXZpY2Utc2VjcmV0LXZhbHVlLWhlcmU=");
        Assert.NotEqual(outcome.SubjectId, otherSecret!.SubjectId);
        var plainSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("LiveQs"))).ToLowerInvariant();
        Assert.DoesNotContain(plainSha256[..16], outcome.SubjectId, StringComparison.Ordinal);
    }

    [Fact]
    public void UnexecutableRegexRuleDegradesToNoMatch()
    {
        var ruleSet = RuleSet("""
            { "rule_set_version": 1, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "bad.regex", "platform": "windows", "kind": "title_regex", "pattern": "([unclosed(",
                "priority": 100, "confidence": 1, "subject_entity_id": "svc.x", "dynamic": false,
                "version": 1, "updated_at": null } ] }
            """);
        Assert.Null(ClassificationEngine.Classify(ruleSet, "windows", "app.exe", "anything", TestSecret));
    }

    [Fact]
    public async Task ClassificationCache_RoundTripsThroughSqliteAndSecretIsStable()
    {
        var repository = await CreateRepositoryAsync();
        Assert.Null(await repository.GetCachedRuleSetAsync());
        Assert.Equal(44, (await repository.GetClassificationSecretAsync()).Length);

        // The secret is generated once and never rotates with rule updates.
        var secretBefore = await repository.GetClassificationSecretAsync();
        var ruleSet = RuleSet(CrossPlatformRuleSetJson);
        await repository.SaveCachedRuleSetAsync(ruleSet, DateTimeOffset.UtcNow);
        var cached = await repository.GetCachedRuleSetAsync();
        Assert.NotNull(cached);
        Assert.Equal(7, cached.RuleSetVersion);
        Assert.Equal(3, cached.Rules.Length);
        // The cache replays the fetched document exactly as the server distributed it.
        Assert.Equal("android.bilibili.package", cached.Rules[0].RuleId);
        Assert.Equal(secretBefore, await repository.GetClassificationSecretAsync());
    }

    [Fact]
    public async Task RuleSync_UpdatesCacheOnSuccessAndKeepsTheLastVersionWhenOffline()
    {
        var repository = await CreateRepositoryAsync();
        var settings = new AppSettings { ServerBaseUrl = "http://127.0.0.1:8787", DeviceToken = "lqdev_token" };
        var clock = new MutableTimeProvider();

        var offlineSync = new ClassificationRuleSync(new SingleClientFactory(new FailingHandler()), repository, clock, NullLogger<ClassificationRuleSync>.Instance);
        var offline = await offlineSync.RefreshAsync(settings, CancellationToken.None);
        Assert.Null(offline); // nothing cached yet, but the failure was not fatal

        var handler = new RecordingGetHandler(CrossPlatformRuleSetJson);
        var onlineSync = new ClassificationRuleSync(new SingleClientFactory(handler), repository, clock, NullLogger<ClassificationRuleSync>.Instance);
        clock.Advance(ClassificationRuleSync.RefreshInterval + TimeSpan.FromMinutes(1));
        var refreshed = await onlineSync.RefreshAsync(settings, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Equal(7, refreshed.RuleSetVersion);
        Assert.Equal("/api/v1/classification/ruleset", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer lqdev_token", handler.Request.Headers.Authorization!.ToString());

        var cached = await repository.GetCachedRuleSetAsync();
        Assert.NotNull(cached);

        // Outage: the fetch fails but the last successful version stays
        // cached and executable (offline execution).
        clock.Advance(ClassificationRuleSync.RefreshInterval + TimeSpan.FromMinutes(1));
        var outageSync = new ClassificationRuleSync(new SingleClientFactory(new FailingHandler()), repository, clock, NullLogger<ClassificationRuleSync>.Instance);
        var duringOutage = await outageSync.RefreshAsync(settings, CancellationToken.None);
        Assert.NotNull(duringOutage);
        Assert.Equal(7, duringOutage.RuleSetVersion);
    }

    [Fact]
    public async Task RuleSync_WithinTheRefreshIntervalServesTheCacheWithoutAnotherFetch()
    {
        var handler = new RecordingGetHandler(CrossPlatformRuleSetJson);
        var repository = new FakeRuleStore();
        var sync = new ClassificationRuleSync(new SingleClientFactory(handler), repository, new MutableTimeProvider(), NullLogger<ClassificationRuleSync>.Instance);

        _ = await sync.RefreshAsync(new AppSettings(), CancellationToken.None);
        var second = await sync.RefreshAsync(new AppSettings(), CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(1, handler.RequestCount); // bounded cadence: no second fetch inside the interval
    }

    [Fact]
    public async Task UploadEnvelope_CarriesExplainableLabelsAndNeverTheRawTitle()
    {
        var repository = await CreateRepositoryAsync();
        await repository.SaveCachedRuleSetAsync(RuleSet(CrossPlatformRuleSetJson), DateTimeOffset.UtcNow);
        var settings = new AppSettings
        {
            CloudSyncEnabled = true,
            ServerBaseUrl = "http://127.0.0.1:8787",
            DeviceToken = "lqdev_test_token",
            DeviceId = "device-1",
        };
        var item = new SyncQueueItem(1, 0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
            "msedge.exe", "Microsoft Edge", "哔哩哔哩 (゜-゜)つロ bilibili - Edge", "titlehash",
            IsAfk: false, IsAudioPlaying: false, IsFullscreen: false, SyncVersion: 3, Finalized: false);

        var outcome = CloudSyncClient.ClassifyItem(item, await repository.GetCachedRuleSetAsync(), await repository.GetClassificationSecretAsync());
        var envelope = CloudSyncClient.ToEnvelope(item, settings, "install-guid", DateTimeOffset.UtcNow, outcome);

        Assert.Equal("svc.bilibili", envelope.Payload.SubjectId);
        Assert.NotNull(envelope.Payload.Classification);
        Assert.Equal("edge.bilibili.title", envelope.Payload.Classification.RuleId);
        Assert.Equal(3, envelope.Payload.Classification.RuleVersion);
        Assert.Equal(0.8, envelope.Payload.Classification.Confidence);

        // The wire shape holds only the explainable labels.
        var json = JsonSerializer.Serialize(envelope, ContractJson.Options);
        Assert.DoesNotContain("bilibili - Edge", json, StringComparison.Ordinal); // raw title stays local
        Assert.DoesNotContain("window_title", json, StringComparison.Ordinal);
        Assert.Contains("\"subject_id\":\"svc.bilibili\"", json, StringComparison.Ordinal);

        // AFK intervals carry no subject: there is no activity to name.
        var afkItem = item with { IsAfk = true };
        Assert.Null(CloudSyncClient.ClassifyItem(afkItem, await repository.GetCachedRuleSetAsync(), await repository.GetClassificationSecretAsync()));

        // Without rules (fresh install, never fetched) nothing is inferred.
        var emptyStore = new FakeRuleStore();
        Assert.Null(CloudSyncClient.ClassifyItem(item, await emptyStore.GetCachedRuleSetAsync(), "secret"));
    }

    private static ClassificationRuleSet RuleSet(string json) =>
        JsonSerializer.Deserialize<ClassificationRuleSet>(json, ContractJson.Options)
        ?? throw new InvalidOperationException("The golden rule set JSON must deserialize.");

    private static async Task<SqliteActivityRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteActivityRepository(new TestPaths(), TimeProvider.System);
        await repository.InitializeAsync();
        return repository;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory)) Directory.Delete(_paths.DataDirectory, true);
    }

    private readonly TestPaths _paths = new();

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

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    /// <summary>Records rule-set GET requests and replies with a canned JSON document.</summary>
    private sealed class RecordingGetHandler(string jsonResponse) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>In-memory rule store isolating refresh-cadence behavior from SQLite.</summary>
    private sealed class FakeRuleStore : IClassificationRuleStore
    {
        public ClassificationRuleSet? Cached { get; private set; }

        public Task<ClassificationRuleSet?> GetCachedRuleSetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Cached);

        public Task SaveCachedRuleSetAsync(ClassificationRuleSet ruleSet, DateTimeOffset fetchedAt, CancellationToken cancellationToken = default)
        {
            Cached = ruleSet;
            return Task.CompletedTask;
        }

        public Task<string> GetClassificationSecretAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TestSecret);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
