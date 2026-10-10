using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Tests;

public sealed class DeviceStateTimelineTests
{
    private static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-10-08T{time}+08:00");

    [Fact]
    public void ModernStandbyWithBriefLockScreenSample_RemainsOneAwayEpisode()
    {
        var original = new ActivitySegment(1, At("00:00:00").AddMinutes(-13).AddSeconds(-47), At("00:15:13"),
            "zcode.exe", "IDE", "", true, false, false, "开发", "#007AFF");
        var unlocking = new ActivitySegment(2, At("00:18:14"), At("00:18:19"),
            "explorer.exe", "Explorer", "UnlockingWindow", true, false, false, "", "#007AFF");
        var lockScreen = new ActivitySegment(3, At("00:32:09"), At("00:32:14"),
            "lockapp.exe", "Windows 锁屏", "", false, false, true, "", "#007AFF");
        var events = new[]
        {
            new DeviceStateEvent("506/1", At("00:15:11"), DeviceStateKind.Sleep, true, "system:modern", "现代待机", 506, 1),
            new DeviceStateEvent("507/2", At("00:32:07"), DeviceStateKind.Sleep, false, "system:modern", "现代待机", 507, 2)
        };
        var range = new DateRange(At("00:00:00"), At("00:00:00").AddDays(1));

        var episode = Assert.Single(TimelinePresentation.Build([original, unlocking, lockScreen], range, stateEvents: events));

        Assert.True(episode.IsAfk);
        Assert.Equal(At("00:00:00"), episode.StartedAt);
        Assert.Equal(At("00:32:14"), episode.EndedAt);
        Assert.Equal(TimeSpan.Zero, episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromSeconds(923), episode.ObservedDuration);
        Assert.False(lockScreen.IsAfk);
        Assert.Contains(episode.Slices, slice => ReferenceEquals(slice.Record, lockScreen));
        Assert.Equal(3, episode.RecordCount);
        Assert.Equal(TimeSpan.FromSeconds(1009), episode.SystemCoveredDuration);
        Assert.Equal(TimeSpan.Zero, episode.RecoveryCoveredDuration);
        Assert.Empty(episode.RecoveryWindows);
        Assert.True(episode.ContinuesBeforeRange);
        var row = new TimelineEpisodeViewModel(episode);
        row.SelectPositionCommand.Execute(0.7d);
        Assert.Empty(row.Marks);
        Assert.Contains("系统补充", row.Coverage);
        Assert.Contains("从前一天延续", row.Flags);
        Assert.Contains("506 → 507", Assert.Single(row.StateEvidenceRows).Source);
    }

    [Fact]
    public void UnknownOrUnclosedState_DoesNotFillMissingRecords()
    {
        var records = new[] { Record(1, 0, 60, true), Record(2, 600, 660, true) };
        var events = new[] { State("start", 60, true) };

        var episodes = TimelinePresentation.Build(records, Range(), stateEvents: events);

        Assert.Equal(3, episodes.Count);
        Assert.Equal(TimeSpan.FromMinutes(9), Assert.Single(episodes, episode => episode.IsGap).Span);
        Assert.All(episodes, episode => Assert.Equal(TimeSpan.Zero, episode.SystemCoveredDuration));
    }

    [Fact]
    public void ClosedStateOnly_ProvidesAwayEvidenceWithoutInventingSamples()
    {
        var episode = Assert.Single(TimelinePresentation.Build([], Range(), stateEvents:
            [State("start", 60, true), State("end", 660, false)]));

        Assert.True(episode.IsAfk);
        Assert.False(episode.IsGap);
        Assert.Empty(episode.Slices);
        Assert.Equal(0, episode.RecordCount);
        Assert.Equal(TimeSpan.Zero, episode.ObservedDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.SystemCoveredDuration);
    }

    [Fact]
    public void PartiallyCoveredGap_PreservesItsUnexplainedRemainder()
    {
        var episodes = TimelinePresentation.Build([Record(1, 0, 60, true), Record(2, 660, 720, true)], Range(),
            stateEvents: [State("start", 60, true), State("end", 300, false)]);

        Assert.Equal(TimeSpan.FromMinutes(4), Assert.Single(episodes, episode => episode.IsGap).Span);
        Assert.Equal(TimeSpan.FromMinutes(4), TimeSpan.FromTicks(episodes.Sum(episode => episode.SystemCoveredDuration.Ticks)));
        Assert.Equal(TimeSpan.FromMinutes(2).Ticks, episodes.Sum(episode => episode.RecoveryCoveredDuration.Ticks));
    }

    [Fact]
    public void OverlappingSources_AreCountedOnceAndBothRemainInspectable()
    {
        var states = new[]
        {
            State("sleep-start", 60, true), State("sleep-end", 180, false),
            State("lock-start", 120, true) with { Kind = DeviceStateKind.Locked, Source = "runtime:test:lock" },
            State("lock-end", 240, false) with { Kind = DeviceStateKind.Locked, Source = "runtime:test:lock" }
        };
        var episode = Assert.Single(TimelinePresentation.Build([], Range(), stateEvents: states));

        Assert.Equal(TimeSpan.FromMinutes(3), episode.SystemCoveredDuration);
        Assert.Equal(2, episode.StateEvidence.Count);
    }

    [Fact]
    public void ShortSystemState_KeepsTheActivityTogetherAndRetainsOriginalFlags()
    {
        var record = Record(1, 0, 600, false);
        var episode = Assert.Single(TimelinePresentation.Build([record], Range(),
            stateEvents: [State("start", 300, true), State("end", 420, false)]));

        Assert.Equal(TimeSpan.FromMinutes(8), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ObservedDuration);
        Assert.Equal(TimeSpan.Zero, episode.SystemCoveredDuration);
        Assert.Equal(1, episode.RecordCount);
        Assert.False(record.IsAfk);
        Assert.Equal(0, episode.TitleChanges);
        var row = new TimelineEpisodeViewModel(episode);
        row.SelectPositionCommand.Execute(0.6d);
        Assert.Single(row.RawRecords);
        Assert.Contains("系统状态：非活跃", row.RawRecords[0].Flags);
    }

    [Fact]
    public void AcrossMidnight_PreservesTheSameEvidenceAndMarksContinuation()
    {
        var events = new[]
        {
            new DeviceStateEvent("start", At("00:00:00").AddMinutes(-10), DeviceStateKind.Sleep, true, "system:test", "现代待机"),
            new DeviceStateEvent("end", At("00:10:00"), DeviceStateKind.Sleep, false, "system:test", "现代待机")
        };
        var before = Assert.Single(TimelinePresentation.Build([], new DateRange(At("00:00:00").AddDays(-1), At("00:00:00")), stateEvents: events));
        var after = Assert.Single(TimelinePresentation.Build([], Range(), stateEvents: events));

        Assert.True(before.ContinuesAfterRange);
        Assert.True(after.ContinuesBeforeRange);
        Assert.Equal(before.StateEvidence, after.StateEvidence);
        Assert.Equal(TimeSpan.FromMinutes(10), before.SystemCoveredDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), after.SystemCoveredDuration);
    }

    [Fact]
    public void EarlierPages_RestoreSamplingCoverageWithoutChangingSystemEvidence()
    {
        var states = new[] { State("start", 60, true), State("end", 660, false) };
        var later = Record(2, 600, 660, true);
        var partial = Assert.Single(TimelinePresentation.Build([later], Range(), true, states));
        var complete = Assert.Single(TimelinePresentation.Build([later, Record(1, 0, 60, true)], Range(), false, states));

        Assert.True(partial.IsPartial);
        Assert.Equal(later.StartedAt, partial.StartedAt);
        Assert.Equal(TimeSpan.Zero, partial.SystemCoveredDuration);
        Assert.False(complete.IsPartial);
        Assert.Equal(2, complete.RecordCount);
        Assert.Single(complete.StateEvidence);
        Assert.Equal(TimeSpan.FromMinutes(2), complete.ObservedDuration);
        Assert.Equal(TimeSpan.FromMinutes(9), complete.SystemCoveredDuration);
    }

    [Fact]
    public void EarlierEvidence_DoesNotDescribeUnloadedSamplingAsMissing()
    {
        var states = new[] { State("start", 60, true), State("end", 660, false) };
        var loaded = Record(2, 1200, 1800, false);
        var partial = Assert.Single(TimelinePresentation.Build([loaded], Range(), true, states));

        Assert.True(partial.IsPartial);
        Assert.Equal(loaded.StartedAt, partial.StartedAt);
        Assert.Empty(partial.StateEvidence);
        Assert.Equal(TimeSpan.Zero, partial.SystemCoveredDuration);
    }

    [Fact]
    public void DashboardAndTimeline_ExcludeConfirmedInactivityWithoutAddingMissingTime()
    {
        var record = Record(1, 0, 600, false);
        var states = new[] { State("start", 300, true), State("end", 900, false) };
        var dashboard = DashboardStatistics.Calculate([record], Range(), TimeZoneInfo.Local, states);
        var episodes = TimelinePresentation.Build([record], Range(), stateEvents: states);

        Assert.Equal(episodes.Sum(episode => episode.ActiveDuration.Ticks), dashboard.ActiveDuration.Ticks);
        Assert.Equal(TimeSpan.FromMinutes(5), dashboard.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), dashboard.AfkDuration);
        Assert.Equal(dashboard.ActiveDuration, Assert.Single(dashboard.Apps).Duration);
        Assert.Equal(dashboard.ActiveDuration.Ticks, dashboard.ActiveBuckets.Sum(bucket => bucket.ActiveDuration.Ticks));
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, episodes.Sum(episode => episode.SystemCoveredDuration.Ticks));
        Assert.False(record.IsAfk);
    }

    [Fact]
    public void SourceRestarts_DoNotCloseAnUnobservedLockInterval()
    {
        var states = new[]
        {
            State("start", 0, true) with { Kind = DeviceStateKind.Locked, Source = "runtime:first" },
            State("end", 600, false) with { Kind = DeviceStateKind.Locked, Source = "runtime:second" }
        };

        Assert.Empty(DeviceStateTimeline.Pair(states));
    }

    [Fact]
    public void FreshSleepAfterMissingResume_DoesNotCoverTheInterveningUnknownGap()
    {
        var episodes = TimelinePresentation.Build([Record(1, 0, 60, true), Record(2, 600, 660, true)], Range(),
            stateEvents: [State("unmatched", 60, true), State("new-sleep", 600, true), State("wake", 660, false)]);

        Assert.Equal(TimeSpan.FromMinutes(9), Assert.Single(episodes, episode => episode.IsGap).Span);
        var evidence = Assert.Single(episodes.SelectMany(episode => episode.StateEvidence));
        Assert.Equal("new-sleep", evidence.Start.Key);
    }

    [Theory]
    [InlineData("lockapp.exe", 0, false, true)]
    [InlineData("LOGONUI.EXE", 0, false, true)]
    [InlineData("msedge.exe", 0, true, true)]
    [InlineData("msedge.exe", 0, false, false)]
    [InlineData("msedge.exe", 61, false, true)]
    public void LockAndIdleClassification_IsIndependentOfFullscreen(string app, double idle, bool locked, bool expected)
    {
        Assert.Equal(expected, WindowsActivityState.IsAfk(app, idle, 60, locked));
    }

    [Fact]
    public void HistoricalLockScreen_DoesNotContributeToApplicationUsage()
    {
        var record = Record(1, 0, 60, false) with
        {
            AppId = "lockapp.exe",
            IsFullscreen = true
        };
        var dashboard = DashboardStatistics.Calculate([record], Range(), TimeZoneInfo.Local);

        Assert.Equal(TimeSpan.Zero, dashboard.ActiveDuration);
        Assert.Empty(dashboard.Apps);
        Assert.Equal(TimeSpan.FromMinutes(1), dashboard.AfkDuration);
        Assert.All(dashboard.ActiveBuckets, bucket => Assert.Equal(TimeSpan.Zero, bucket.ActiveDuration));
        Assert.False(record.IsAfk);
    }

    private static DateRange Range() => new(At("00:00:00"), At("00:00:00").AddDays(1));
    private static ActivitySegment Record(long id, int start, int end, bool afk) => new(id,
        At("00:00:00").AddSeconds(start), At("00:00:00").AddSeconds(end), "code", "IDE", "title", afk, false, false, "", "#007AFF");
    private static DeviceStateEvent State(string key, int seconds, bool entering) => new(key,
        At("00:00:00").AddSeconds(seconds), DeviceStateKind.Sleep, entering, "system:test", "现代待机");
}
