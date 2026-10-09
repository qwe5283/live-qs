using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;

namespace LiveQs.Windows.Tests;

public sealed class ReliabilityRegressionTests : IDisposable
{
    private readonly TestDataPaths _paths = new();
    private readonly TestClock _clock = new();

    [Fact]
    public async Task AcknowledgingCheckpoint_PreservesFinalRevisionCreatedDuringUpload()
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(0), TimeSpan.FromSeconds(10));
        var uploaded = Assert.Single(await repository.GetPendingSyncAsync(100, _clock.GetUtcNow()));

        await repository.RecordSampleAsync(Sample(10), TimeSpan.FromSeconds(10));
        await repository.RecordSampleAsync(Sample(20, "b.exe"), TimeSpan.FromSeconds(10));
        await repository.MarkSyncedAsync([uploaded.Revision], _clock.GetUtcNow());

        var pending = await repository.GetPendingSyncAsync(100, _clock.GetUtcNow());
        var finalized = Assert.Single(pending, item => item.SegmentId == uploaded.SegmentId);
        Assert.True(finalized.Finalized);
        Assert.Equal(3, finalized.SyncVersion);
        Assert.Equal(_clock.GetUtcNow().AddSeconds(20), finalized.EndedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamplingAfterUploadFailure_PreservesRetryDeadline(bool changeApplication)
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(0), TimeSpan.FromSeconds(10));
        var failed = Assert.Single(await repository.GetPendingSyncAsync(100, _clock.GetUtcNow()));
        var retryAt = _clock.GetUtcNow().AddHours(1);
        await repository.MarkSyncFailedAsync([failed.SegmentId], "offline", retryAt);

        await repository.RecordSampleAsync(
            Sample(10, changeApplication ? "b.exe" : "a.exe"), TimeSpan.FromSeconds(10));

        var due = await repository.GetPendingSyncAsync(100, _clock.GetUtcNow());
        Assert.DoesNotContain(due, item => item.SegmentId == failed.SegmentId);
        var retryable = await repository.GetPendingSyncAsync(100, retryAt);
        Assert.Equal(1, retryable.Single(item => item.SegmentId == failed.SegmentId).AttemptCount);
    }

    [Fact]
    public async Task RejectingAnOldRevision_DoesNotPermanentlyDisableTheNewRevision()
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(0), TimeSpan.FromSeconds(10));
        var uploaded = Assert.Single(await repository.GetPendingSyncAsync(100, _clock.GetUtcNow()));

        await repository.RecordSampleAsync(Sample(10), TimeSpan.FromSeconds(10));
        await repository.MarkPermanentAsync([uploaded.Revision], "invalid_event", _clock.GetUtcNow());

        var current = Assert.Single(await repository.GetPendingSyncAsync(100, _clock.GetUtcNow()));
        Assert.Equal(2, current.SyncVersion);
        Assert.Equal(0, (await repository.GetSyncOverviewAsync()).PermanentFailureCount);
    }

    [Fact]
    public async Task ExcludedApplication_BreaksThePreviousActivityInterval()
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(0), TimeSpan.FromSeconds(10));
        await repository.SaveApplicationRuleAsync(new ApplicationRule("b.exe", "", "", true));

        await repository.RecordSampleAsync(Sample(10, "b.exe"), TimeSpan.FromSeconds(10));
        await repository.RecordSampleAsync(Sample(20, "b.exe"), TimeSpan.FromSeconds(10));
        await repository.RecordSampleAsync(Sample(30), TimeSpan.FromSeconds(10));

        var dashboard = await repository.GetDashboardAsync(Range);
        var records = await repository.GetTimelineAsync(Range);
        Assert.Equal(TimeSpan.FromSeconds(20), dashboard.ActiveDuration);
        Assert.Equal(2, records.Count);
        Assert.DoesNotContain(records, record => record.AppId == "b.exe");
    }

    [Fact]
    public async Task RestartIntoExcludedApplication_StopsThePreviousEstimateAtTheExcludedSample()
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(0), TimeSpan.FromSeconds(10));
        await repository.SaveApplicationRuleAsync(new ApplicationRule("b.exe", "", "", true));

        var reopened = await CreateRepositoryAsync();
        await reopened.RecordSampleAsync(Sample(7, "b.exe"), TimeSpan.FromSeconds(10));

        var record = Assert.Single(await reopened.GetTimelineAsync(Range));
        var pending = Assert.Single(await reopened.GetPendingSyncAsync(100, _clock.GetUtcNow()));
        Assert.Equal(TimeSpan.FromSeconds(7), record.Duration);
        Assert.Equal(record.EndedAt, pending.EndedAt);
        Assert.True(pending.Finalized);
        Assert.Equal(2, pending.SyncVersion);
    }

    [Fact]
    public async Task RestartBeforeLastSampleEnds_UsesConsistentForegroundDurations()
    {
        var repository = await CreateRepositoryAsync();
        await repository.RecordSampleAsync(Sample(0), TimeSpan.FromSeconds(10));

        var reopened = await CreateRepositoryAsync();
        await reopened.RecordSampleAsync(Sample(7, "b.exe"), TimeSpan.FromSeconds(10));

        var records = await reopened.GetTimelineAsync(Range);
        var dashboard = await reopened.GetDashboardAsync(Range);
        var episodes = TimelinePresentation.Build(records, Range);
        Assert.Equal(TimeSpan.FromSeconds(17), dashboard.ActiveDuration);
        Assert.Equal(dashboard.ActiveDuration.Ticks, dashboard.ActiveBuckets.Sum(bucket => bucket.ActiveDuration.Ticks));
        Assert.Equal(dashboard.ActiveDuration.Ticks, episodes.Sum(episode => episode.ActiveDuration.Ticks));
        Assert.Equal(TimeSpan.FromSeconds(7), dashboard.Apps.Single(app => app.AppId == "a.exe").Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), records.Single(record => record.AppId == "a.exe").Duration);
    }

    [Fact]
    public async Task LaterSampleBeforeQueryStart_DoesNotReviveTheSupersededEstimate()
    {
        var repository = await CreateRepositoryAsync();
        var midnight = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        await repository.RecordSampleAsync(
            Sample(0) with
            {
                CapturedAt = midnight.AddSeconds(-2)
            }, TimeSpan.FromSeconds(10));
        await repository.RecordSampleAsync(
            Sample(0, "b.exe") with
            {
                CapturedAt = midnight.AddSeconds(-1)
            }, TimeSpan.FromSeconds(1));
        var nextDay = new DateRange(midnight, midnight.AddDays(1));
        var twoDays = new DateRange(midnight.AddDays(-1), midnight.AddDays(1));

        var daily = await repository.GetDashboardAsync(nextDay);
        var combined = await repository.GetDashboardAsync(twoDays);
        var page = await repository.GetTimelinePageAsync(nextDay, 100);

        Assert.Equal(TimeSpan.Zero, daily.ActiveDuration);
        Assert.Equal(TimeSpan.FromSeconds(2), combined.ActiveDuration);
        Assert.All(daily.ActiveBuckets, bucket => Assert.Equal(TimeSpan.Zero, bucket.ActiveDuration));
        Assert.Empty(page.Items);
        Assert.Empty(await repository.GetTimelineAsync(nextDay));
    }

    [Fact]
    public void RegexTimeout_FallsBackToTheNextApplicableRule()
    {
        var rules = new ClassificationRuleSet
        {
            Rules =
            [
                new ClassificationRule
                {
                    RuleId = "slow",
                    Kind = ClassificationRuleKind.TitleRegex,
                    Platform = ClassificationRulePlatform.Windows,
                    Pattern = "(a+)+$",
                    SubjectEntityId = "svc.slow",
                    Priority = 10,
                    Version = 1,
                    Confidence = 1,
                },
                new ClassificationRule
                {
                    RuleId = "fallback",
                    Kind = ClassificationRuleKind.Application,
                    Platform = ClassificationRulePlatform.Windows,
                    Pattern = "a.exe",
                    SubjectEntityId = "svc.fallback",
                    Version = 1,
                    Confidence = 1,
                },
            ],
        };

        var outcome = ClassificationEngine.Classify(rules, "windows", "a.exe", new string('a', 1023) + "!", "local");

        Assert.Equal("svc.fallback", outcome?.SubjectId);
    }

    private DateRange Range => new(_clock.GetUtcNow(), _clock.GetUtcNow().AddHours(1));

    private ActivitySample Sample(int seconds, string applicationId = "a.exe") => new(
        _clock.GetUtcNow().AddSeconds(seconds), applicationId, applicationId, "", "title", "hash",
        0, false, false, false, null, null);

    private async Task<SqliteActivityRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteActivityRepository(_paths, _clock);
        await repository.InitializeAsync();
        return repository;
    }

    public void Dispose() => _paths.Dispose();
}
