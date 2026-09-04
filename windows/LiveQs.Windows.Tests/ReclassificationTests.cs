using System.Globalization;
using System.Text;
using System.Text.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Infrastructure.Classification;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

/// <summary>
/// Explicit historical reclassification: the planner's unchanged-versus-submit
/// decision, the candidate query (finalized, non-AFK, acknowledged segments
/// only), the executor pass end to end over a scripted HTTP surface, the
/// stale_revision yield to manual Owner corrections, and deferral while the
/// target rule set version is unavailable.
/// </summary>
public sealed class ReclassificationTests : IDisposable
{
    private readonly TestPaths _paths = new();

    private const string RuleSetV1 = """
        {
          "rule_set_version": 1, "updated_at": "2026-08-01T00:00:00.000Z", "entities": [],
          "rules": [
            { "rule_id": "edge.bilibili.title", "platform": "windows", "kind": "title_keyword",
              "pattern": "bilibili", "priority": 10, "confidence": 0.8,
              "subject_entity_id": "svc.old", "dynamic": false, "version": 1, "updated_at": null }
          ]
        }
        """;

    /// <summary>The Owner improved the rule: same pattern, new subject, bumped rule version.</summary>
    private const string RuleSetV2 = """
        {
          "rule_set_version": 2, "updated_at": "2026-08-02T00:00:00.000Z", "entities": [],
          "rules": [
            { "rule_id": "edge.bilibili.title", "platform": "windows", "kind": "title_keyword",
              "pattern": "bilibili", "priority": 10, "confidence": 0.8,
              "subject_entity_id": "svc.new", "dynamic": false, "version": 2, "updated_at": null }
          ]
        }
        """;

    private static readonly Guid TaskId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    // --- Planner (pure) ---

    [Fact]
    public void Planner_SubmitsWhenTheRecomputedOutcomeDiffers()
    {
        var segment = Segment(syncVersion: 4, uploadOutcome: new ClassificationOutcome("svc.old", "edge.bilibili.title", 1, 0.8));
        var decision = ReclassificationPlanner.Decide(segment, new ClassificationOutcome("svc.new", "edge.bilibili.title", 2, 0.8));

        Assert.Equal(ReclassificationAction.SubmitRevision, decision.Action);
        Assert.Equal(5, decision.Segment.SyncVersion);
        Assert.Equal("svc.new", decision.Outcome!.SubjectId);
        Assert.Equal(4, segment.SyncVersion); // the original stays untouched
    }

    [Fact]
    public void Planner_LeavesUnchangedWhenTheRecordedOutcomeStillMatches()
    {
        var recorded = new ClassificationOutcome("svc.new", "edge.bilibili.title", 2, 0.8);
        var segment = Segment(syncVersion: 4, uploadOutcome: recorded);

        var decision = ReclassificationPlanner.Decide(segment, new ClassificationOutcome("svc.new", "edge.bilibili.title", 2, 0.8));

        Assert.Equal(ReclassificationAction.LeaveUnchanged, decision.Action);
    }

    [Fact]
    public void Planner_AddsASubjectButNeverStripsAnUnknownLegacyOutcome()
    {
        // A legacy segment (no recorded outcome) that now matches: gain a subject.
        var add = ReclassificationPlanner.Decide(Segment(syncVersion: 1, uploadOutcome: null), new ClassificationOutcome("svc.new", "edge.bilibili.title", 2, 0.8));
        Assert.Equal(ReclassificationAction.SubmitRevision, add.Action);

        // A legacy segment the engine no longer matches: stripping an
        // unverified subject would be guesswork, so nothing is submitted.
        var strip = ReclassificationPlanner.Decide(Segment(syncVersion: 1, uploadOutcome: null), null);
        Assert.Equal(ReclassificationAction.LeaveUnchanged, strip.Action);
    }

    [Fact]
    public void Planner_SubmitsASubjectStripOnlyWhenTheServerOutcomeIsKnown()
    {
        // The device knows the server holds the removed rule's outcome, so
        // re-submitting without a subject is a deliberate change, not a guess.
        var recorded = new ClassificationOutcome("svc.old", "edge.bilibili.title", 1, 0.8);
        var decision = ReclassificationPlanner.Decide(Segment(syncVersion: 7, uploadOutcome: recorded), null);

        Assert.Equal(ReclassificationAction.SubmitRevision, decision.Action);
        Assert.Null(decision.Outcome);
        Assert.Equal(8, decision.Segment.SyncVersion);
    }

    // --- Candidate query and local persistence ---

    [Fact]
    public async Task Candidates_ContainOnlyFinalizedNonAfkAcknowledgedSegmentsInRange()
    {
        var repository = await CreateRepositoryAsync();
        var from = DateTimeOffset.UtcNow.AddHours(-3);
        var to = DateTimeOffset.UtcNow.AddHours(1);

        var active = await SeedFinalizedSegmentAsync(repository, from.AddHours(1), appId: "active.exe");
        var afk = await SeedFinalizedSegmentAsync(repository, from.AddHours(1).AddMinutes(12), appId: "afk.exe", afk: true);
        var queued = await SeedFinalizedSegmentAsync(repository, from.AddHours(1).AddMinutes(24), appId: "queued.exe");
        var outside = await SeedFinalizedSegmentAsync(repository, DateTimeOffset.UtcNow.AddHours(2), appId: "outside.exe");
        await repository.MarkSyncedAsync(new[] { active.SegmentId, afk.SegmentId, outside.SegmentId }, DateTimeOffset.UtcNow);

        await repository.RecordUploadOutcomeAsync(active.SegmentId, new ClassificationOutcome("svc.old", "edge.bilibili.title", 1, 0.8));

        var candidates = await repository.GetReclassificationCandidatesAsync(from, to, 100, 0);

        var candidate = Assert.Single(candidates);
        Assert.Equal(active.SegmentId, candidate.SegmentId);
        Assert.False(candidate.IsAfk);
        Assert.True(candidate.Finalized);
        Assert.Equal("svc.old", candidate.UploadOutcome!.SubjectId);
        // The queued (outbox-owned), AFK, and out-of-range seeds are all absent:
        // exactly one candidate means every exclusion held.
        Assert.DoesNotContain(candidates, item => item.SegmentId == queued.SegmentId);
        Assert.DoesNotContain(candidates, item => item.SegmentId == afk.SegmentId);
        Assert.DoesNotContain(candidates, item => item.SegmentId == outside.SegmentId);
    }

    [Fact]
    public async Task RecordReclassified_AdvancesTheLocalRevisionAndRecordedOutcome()
    {
        var repository = await CreateRepositoryAsync();
        var seeded = await SeedFinalizedSegmentAsync(repository, DateTimeOffset.UtcNow.AddMinutes(-5));
        // Acknowledge the seed so it leaves the outbox and becomes a candidate.
        await repository.MarkSyncedAsync(new[] { seeded.SegmentId }, DateTimeOffset.UtcNow);

        await repository.RecordReclassifiedAsync(seeded.SegmentId, seeded.SyncVersion + 1, new ClassificationOutcome("svc.new", "edge.bilibili.title", 2, 0.8));

        var candidate = Assert.Single(await repository.GetReclassificationCandidatesAsync(null, null, 100, 0));
        Assert.Equal(seeded.SyncVersion + 1, candidate.SyncVersion);
        Assert.Equal("svc.new", candidate.UploadOutcome!.SubjectId);
        Assert.Equal(2, candidate.UploadOutcome.RuleVersion);
    }

    // --- Executor pass end to end ---

    [Fact]
    public async Task Worker_ReissuesChangedSegmentsAndReportsCounts()
    {
        var (handler, repository, worker, segmentId, installId, storedRevision) = await CreateExecutorAsync((request, body) => Respond(request, body, "accepted"));

        await worker.RunOnceAsync(CancellationToken.None);

        var batch = Assert.Single(handler.Requests, entry => entry.Path.EndsWith("/api/v1/events/batch", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(batch.Body))
        {
            var envelope = document.RootElement.GetProperty("events")[0];
            Assert.Equal(EventIds.ForSegment("device-1", installId, segmentId).ToString(), envelope.GetProperty("event_id").GetString());
            Assert.Equal(storedRevision + 1, envelope.GetProperty("revision").GetInt32());
            Assert.Equal("final", envelope.GetProperty("finalization_state").GetString());
            Assert.Equal("svc.new", envelope.GetProperty("payload").GetProperty("subject_id").GetString());
            Assert.Equal(2, envelope.GetProperty("payload").GetProperty("classification").GetProperty("rule_version").GetInt32());
            // The raw title never leaves the device, even during reclassification.
            Assert.DoesNotContain("机密文档", batch.Body, StringComparison.Ordinal);
        }

        var report = Assert.Single(handler.Requests, entry => entry.Path.Contains("/device-reports", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(report.Body))
        {
            Assert.Equal("windows", document.RootElement.GetProperty("platform").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("scanned").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("reclassified").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("unchanged").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("failed").GetInt32());
        }

        // Local state mirrors the acknowledged revision so later passes converge.
        var candidate = Assert.Single(await repository.GetReclassificationCandidatesAsync(null, null, 100, 0));
        Assert.Equal(storedRevision + 1, candidate.SyncVersion);
        Assert.Equal("svc.new", candidate.UploadOutcome!.SubjectId);

        // A second pass on the same task finds nothing to change and burns no revision.
        handler.Requests.Clear();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, entry => entry.Path.EndsWith("/api/v1/events/batch", StringComparison.Ordinal));
        var secondReport = Assert.Single(handler.Requests, entry => entry.Path.Contains("/device-reports", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(secondReport.Body))
        {
            Assert.Equal(1, document.RootElement.GetProperty("scanned").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("reclassified").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("unchanged").GetInt32());
        }
    }

    [Fact]
    public async Task Worker_YieldsToManualCorrectionsByAnsweringStaleRevision()
    {
        var (handler, repository, worker, _, _, storedRevision) = await CreateExecutorAsync((request, body) => Respond(request, body, "stale_revision"));

        await worker.RunOnceAsync(CancellationToken.None);

        var report = Assert.Single(handler.Requests, entry => entry.Path.Contains("/device-reports", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(report.Body))
        {
            // The human decision stands; the device yields and counts the
            // event as needing no change, never as a failure.
            Assert.Equal(1, document.RootElement.GetProperty("unchanged").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("reclassified").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("failed").GetInt32());
        }
        // The local revision is not bumped above what the server settled, and
        // the attempted interpretation is recorded so later passes skip it.
        var candidate = Assert.Single(await repository.GetReclassificationCandidatesAsync(null, null, 100, 0));
        Assert.Equal(storedRevision, candidate.SyncVersion);
        Assert.NotNull(candidate.UploadOutcome);
    }

    [Fact]
    public async Task Worker_CountsPermanentRejectionsAsFailuresWithoutLocalWrites()
    {
        var (handler, repository, worker, _, _, storedRevision) = await CreateExecutorAsync((request, body) => Respond(request, body, "rejected"));

        await worker.RunOnceAsync(CancellationToken.None);

        var report = Assert.Single(handler.Requests, entry => entry.Path.Contains("/device-reports", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(report.Body);
        Assert.Equal(1, document.RootElement.GetProperty("failed").GetInt32());
        // A rejected reclassification leaves the segment exactly as before.
        var candidate = Assert.Single(await repository.GetReclassificationCandidatesAsync(null, null, 100, 0));
        Assert.Equal(storedRevision, candidate.SyncVersion);
        Assert.Equal("svc.old", candidate.UploadOutcome!.SubjectId);
    }

    [Fact]
    public async Task Worker_DefersWhileTheTargetRuleSetVersionIsUnavailable()
    {
        var repository = await CreateRepositoryAsync();
        var requests = new List<(string Method, string Path, string Body)>();
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.EndsWith("/api/v1/classification/reclassification/tasks/assignment", StringComparison.Ordinal))
            {
                return Json(200, AssignmentBody(targetVersion: 3));
            }
            if (path.EndsWith("/api/v1/classification/ruleset", StringComparison.Ordinal))
            {
                // The server keeps serving v2 while the task targets v3.
                return Json(200, RuleSetV2);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }, requests);
        var factory = new SingleClientFactory(handler);
        await repository.SaveSettingsAsync(CloudSettings());
        await repository.SaveCachedRuleSetAsync(RuleSet(RuleSetV1), DateTimeOffset.UtcNow);
        var worker = CreateWorker(repository, factory);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(requests, entry => entry.Path.EndsWith("/api/v1/events/batch", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, entry => entry.Path.Contains("/device-reports", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_DoesNothingWithoutAnAssignmentOrDisabledSync()
    {
        var repository = await CreateRepositoryAsync();
        var requests = new List<(string Method, string Path, string Body)>();
        var handler = new ScriptedHttpHandler((_, _) => Json(204, ""), requests);
        var factory = new SingleClientFactory(handler);
        var worker = CreateWorker(repository, factory);
        await repository.SaveSettingsAsync(CloudSettings());

        // No open task: the poll answers 204 and nothing else happens.
        await worker.RunOnceAsync(CancellationToken.None);
        var paths = requests.Select(entry => entry.Path).ToArray();
        Assert.Single(paths, path => path.EndsWith("/api/v1/classification/reclassification/tasks/assignment", StringComparison.Ordinal));

        // Sync disabled: no poll at all.
        requests.Clear();
        await repository.SaveSettingsAsync(CloudSettings() with { CloudSyncEnabled = false });
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Empty(requests);
    }

    // --- plumbing ---

    private static HttpResponseMessage Respond(HttpRequestMessage request, string body, string ackStatus)
    {
        var path = request.RequestUri!.PathAndQuery;
        if (path.EndsWith("/api/v1/classification/reclassification/tasks/assignment", StringComparison.Ordinal))
        {
            return Json(200, AssignmentBody(targetVersion: 2));
        }
        if (path.EndsWith("/api/v1/classification/ruleset", StringComparison.Ordinal))
        {
            return Json(200, RuleSetV2);
        }
        if (path.EndsWith("/api/v1/events/batch", StringComparison.Ordinal))
        {
            return Json(200, AckResponse(body, ackStatus));
        }
        if (path.Contains("/device-reports", StringComparison.Ordinal))
        {
            return Json(204, "");
        }
        throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
    }

    private static string AssignmentBody(int targetVersion) =>
        "{\"task_id\":\"" + TaskId.ToString("D").ToLowerInvariant()
        + "\",\"target_rule_set_version\":" + targetVersion.ToString(CultureInfo.InvariantCulture)
        + ",\"from\":null,\"to\":null}";

    private static SyncQueueItem Segment(int syncVersion, ClassificationOutcome? uploadOutcome) => new(
        SegmentId: 1,
        AttemptCount: 0,
        StartedAt: DateTimeOffset.Parse("2026-08-21T10:00:00.000Z", CultureInfo.InvariantCulture),
        EndedAt: DateTimeOffset.Parse("2026-08-21T10:20:00.000Z", CultureInfo.InvariantCulture),
        AppId: "msedge.exe",
        AppName: "Microsoft Edge",
        WindowTitle: "bilibili 标题 - Edge",
        WindowTitleHash: "titlehash",
        IsAfk: false,
        IsAudioPlaying: false,
        IsFullscreen: false,
        SyncVersion: syncVersion,
        Finalized: true,
        UploadOutcome: uploadOutcome);

    /// <summary>
    /// Creates one finalized segment by sampling an application and then a
    /// different one: inserting a new segment finalizes the previous open
    /// interval (its final revision queued), mirroring the collector's own
    /// merge rule. The returned item is still in the outbox.
    /// </summary>
    private static async Task<SyncQueueItem> SeedFinalizedSegmentAsync(
        SqliteActivityRepository repository, DateTimeOffset startedAt, bool afk = false, string appId = "msedge.exe")
    {
        await repository.RecordSampleAsync(Sample(startedAt, appId, afk), TimeSpan.FromSeconds(10));
        await repository.RecordSampleAsync(Sample(startedAt.AddMinutes(1), "closer.exe", afk: false), TimeSpan.FromSeconds(10));
        var pending = await repository.GetPendingSyncAsync(100, DateTimeOffset.UtcNow.AddMinutes(1));
        return pending.Single(item => item.AppId == appId && item.StartedAt == startedAt.ToUniversalTime());
    }

    private static ActivitySample Sample(DateTimeOffset time, string appId, bool afk) => new(
        time.ToUniversalTime(), appId, "App", "C:\\Program Files\\app.exe", "机密文档 - bilibili 标题",
        $"hash-{appId}-{time.Ticks}", afk ? 120 : 0, afk, false, false, null, null);

    private async Task<(ScriptedHttpHandler Handler, SqliteActivityRepository Repository, ReclassificationWorker Worker, long SegmentId, string InstallId, int StoredRevision)>
        CreateExecutorAsync(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var requests = new List<(string Method, string Path, string Body)>();
        var handler = new ScriptedHttpHandler(respond, requests);
        var factory = new SingleClientFactory(handler);
        var repository = await CreateRepositoryAsync();
        await repository.SaveSettingsAsync(CloudSettings());
        // The cached rule set is one version behind the task target, so the
        // pass must refresh it first — exactly what the ruleset GET serves.
        await repository.SaveCachedRuleSetAsync(RuleSet(RuleSetV1), DateTimeOffset.UtcNow);
        var seeded = await SeedFinalizedSegmentAsync(repository, DateTimeOffset.UtcNow.AddMinutes(-5));
        // The segment is already uploaded and acknowledged: out of the outbox,
        // with the server holding the v1 interpretation of it.
        await repository.MarkSyncedAsync(new[] { seeded.SegmentId }, DateTimeOffset.UtcNow);
        await repository.RecordUploadOutcomeAsync(seeded.SegmentId, new ClassificationOutcome("svc.old", "edge.bilibili.title", 1, 0.8));
        var installId = await repository.GetInstallIdAsync();
        return (handler, repository, CreateWorker(repository, factory), seeded.SegmentId, installId, seeded.SyncVersion);
    }

    private static ReclassificationWorker CreateWorker(SqliteActivityRepository repository, SingleClientFactory factory) =>
        new(
            repository,
            repository,
            new ReclassificationClient(factory, NullLogger<ReclassificationClient>.Instance),
            new CloudSyncClient(factory, repository, repository, TimeProvider.System),
            new ClassificationRuleSync(factory, repository, TimeProvider.System, NullLogger<ClassificationRuleSync>.Instance),
            repository,
            TimeProvider.System,
            NullLogger<ReclassificationWorker>.Instance);

    private static AppSettings CloudSettings() => new()
    {
        CloudSyncEnabled = true,
        ServerBaseUrl = "http://127.0.0.1:8787",
        DeviceToken = "lqdev_test_token",
        DeviceId = "device-1",
    };

    private static ClassificationRuleSet RuleSet(string json) =>
        JsonSerializer.Deserialize<ClassificationRuleSet>(json, ContractJson.Options)
        ?? throw new InvalidOperationException("The golden rule set JSON must deserialize.");

    private static HttpResponseMessage Json(int statusCode, string body) => new((System.Net.HttpStatusCode)statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string AckResponse(string batchBody, string status)
    {
        using var document = JsonDocument.Parse(batchBody);
        var count = document.RootElement.GetProperty("events").GetArrayLength();
        var builder = new StringBuilder("""{"results":[""");
        for (var index = 0; index < count; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append(status == "rejected"
                ? """{"event_id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","revision":3,"status":"rejected","error":{"code":"invalid_event","message":"bad payload"}}"""
                : "{\"event_id\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\",\"revision\":3,\"status\":\"" + status + "\"}");
        }
        builder.Append("]}");
        return builder.ToString();
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

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond, List<(string Method, string Path, string Body)>? requests) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Requests { get; } = requests ?? new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.ToString(), request.RequestUri!.PathAndQuery, body));
            return respond(request, body);
        }
    }
}
