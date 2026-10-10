using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Tests;

public sealed class RecoveryWindowTests
{
    private static readonly DateTimeOffset _start = DateTimeOffset.Parse("2026-10-08T00:00:00+08:00");
    private static readonly DateRange _day = new(_start, _start.AddDays(1));

    [Theory]
    [InlineData(DeviceStateKind.Sleep, "system:sleep", 107)]
    [InlineData(DeviceStateKind.Sleep, "system:recovered-sleep", 1)]
    [InlineData(DeviceStateKind.Shutdown, "system:shutdown", 12)]
    public void LoggedResumeOrBoot_BridgesUntilFirstSampleWithinTwoMinutes(
        DeviceStateKind kind, string source, int eventId)
    {
        var events = States(kind, source, eventId);
        var active = Record(1, 690, 1290);
        var episodes = TimelinePresentation.Build([active], _day, stateEvents: events);
        var away = Assert.Single(episodes, episode => episode.IsAfk);

        Assert.Equal(active.StartedAt, away.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), away.SystemCoveredDuration);
        Assert.Equal(TimeSpan.FromSeconds(90), away.RecoveryCoveredDuration);
        Assert.Equal(TimeSpan.Zero, away.ObservedDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), episodes.Single(episode => !episode.IsAfk).ActiveDuration);
        Assert.DoesNotContain(episodes, episode => episode.IsGap);
        Assert.Equal(_start.AddMinutes(10), events[1].OccurredAt);
        var row = new TimelineEpisodeViewModel(away);
        row.SelectPositionCommand.Execute(0.95d);
        Assert.Contains("恢复缓冲", row.Coverage);
        Assert.Empty(row.RawRecords);
        Assert.Contains(row.StateEvidenceRows, evidence => evidence.Description == "采集恢复窗口");
    }

    [Theory]
    [InlineData("system:modern-standby", 30, false)]
    [InlineData("system:modern-standby", 31, true)]
    [InlineData("system:modern-standby", 90, true)]
    [InlineData("system:modern", 90, true)]
    public void ModernStandby_EndsAtExitAndUsesOrdinaryGapRules(string source, int delay, bool hasGap)
    {
        var events = States(source: source, eventId: 507);
        events[0] = events[0] with
        {
            EventId = 506
        };
        var active = Record(1, 600 + delay, 1200 + delay);
        var episodes = TimelinePresentation.Build([active], _day, stateEvents: events);
        var away = Assert.Single(episodes, episode => episode.IsAfk);

        Assert.Equal(events[1].OccurredAt, away.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), away.SystemCoveredDuration);
        Assert.Equal(506, Assert.Single(away.StateEvidence).Start.EventId);
        Assert.All(episodes, episode =>
        {
            Assert.Empty(episode.RecoveryWindows);
            Assert.Equal(TimeSpan.Zero, episode.RecoveryCoveredDuration);
        });
        Assert.Equal(TimeSpan.FromMinutes(10), episodes.Single(episode => !episode.IsAfk && !episode.IsGap).ActiveDuration);
        if (hasGap)
        {
            var gap = Assert.Single(episodes, episode => episode.IsGap);
            Assert.Equal(events[1].OccurredAt, gap.StartedAt);
            Assert.Equal(active.StartedAt, gap.EndedAt);
            Assert.Equal(TimeSpan.FromSeconds(delay), gap.Span);
        }
        else
        {
            Assert.DoesNotContain(episodes, episode => episode.IsGap);
        }
    }

    [Theory]
    [InlineData("system:modern-standby")]
    [InlineData("system:modern")]
    public void UnpairedModernStandbyExit_DoesNotExplainMissingTime(string source)
    {
        var exit = States(source: source, eventId: 507)[1];
        var episodes = TimelinePresentation.Build([Record(1, 0, 60, true), Record(2, 690, 1290)],
            _day, stateEvents: [exit]);

        Assert.Equal(_start.AddMinutes(1), Assert.Single(episodes, episode => episode.IsAfk).EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(630), Assert.Single(episodes, episode => episode.IsGap).Span);
        Assert.All(episodes, episode => Assert.Empty(episode.RecoveryWindows));
    }

    [Theory]
    [InlineData(120, 0)]
    [InlineData(121, 1)]
    [InlineData(180, 60)]
    public void TwoMinuteDeadline_IsInclusiveAndDoesNotAddThirtySecondGapTolerance(int delay, int gapSeconds)
    {
        var active = Record(1, 600 + delay, 1200 + delay);
        var episodes = TimelinePresentation.Build([active], _day, stateEvents: States());
        var away = Assert.Single(episodes, episode => episode.IsAfk);

        Assert.Equal(_start.AddMinutes(12), away.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(2), away.RecoveryCoveredDuration);
        Assert.Equal(2 + (gapSeconds > 0 ? 1 : 0), episodes.Count);
        if (gapSeconds > 0)
        {
            var gap = Assert.Single(episodes, episode => episode.IsGap);
            Assert.Equal(away.EndedAt, gap.StartedAt);
            Assert.Equal(active.StartedAt, gap.EndedAt);
            Assert.Equal(TimeSpan.FromSeconds(gapSeconds), gap.Span);
        }
    }

    [Fact]
    public void AfkAfterDeadline_StartsANewAwayEpisodeEvenForOneSecondGap()
    {
        var episodes = TimelinePresentation.Build([Record(1, 721, 1321, true)], _day, stateEvents: States());

        Assert.Equal(2, episodes.Count(episode => episode.IsAfk));
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(episodes, episode => episode.IsGap).Span);
    }

    [Fact]
    public void FirstAfkSample_EndsTheWindowAndLaterMissingTimeIsNotBufferedAgain()
    {
        var episodes = TimelinePresentation.Build([Record(1, 660, 670, true), Record(2, 710, 1310)],
            _day, stateEvents: States());

        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, episodes.Sum(episode => episode.RecoveryCoveredDuration.Ticks));
        Assert.Equal(TimeSpan.FromSeconds(40), Assert.Single(episodes, episode => episode.IsGap).Span);
    }

    [Fact]
    public void AlreadySampledResume_DoesNotMaskRealActiveTime()
    {
        var active = Record(1, 590, 1200);
        var episodes = TimelinePresentation.Build([active], _day, stateEvents: States());
        var dashboard = DashboardStatistics.Calculate([active], _day, TimeZoneInfo.Local, States());

        Assert.All(episodes, episode => Assert.Equal(TimeSpan.Zero, episode.RecoveryCoveredDuration));
        Assert.Equal(TimeSpan.FromMinutes(10), dashboard.ActiveDuration);
        Assert.Equal(dashboard.ActiveDuration.Ticks, episodes.Sum(episode => episode.ActiveDuration.Ticks));
        Assert.False(active.IsAfk);
    }

    [Theory]
    [InlineData(DeviceStateKind.Locked, "system:lock")]
    [InlineData(DeviceStateKind.Sleep, "runtime:test")]
    public void UnlockOrRuntimeNotification_DoesNotReceiveTheLogRecoveryWindow(DeviceStateKind kind, string source)
    {
        var episodes = TimelinePresentation.Build([Record(1, 660, 1260)], _day,
            stateEvents: States(kind, source, 0));

        Assert.Equal(TimeSpan.FromMinutes(1), Assert.Single(episodes, episode => episode.IsGap).Span);
        Assert.All(episodes, episode => Assert.Empty(episode.RecoveryWindows));
    }

    [Fact]
    public void UnpairedBoot_OnlyExplainsTheBoundedTimeAfterItsOwnTimestamp()
    {
        var boot = States(DeviceStateKind.Shutdown, "system:shutdown", 12)[1];
        var episodes = TimelinePresentation.Build([Record(1, 0, 60, true), Record(2, 690, 1290, true)],
            _day, stateEvents: [boot]);

        Assert.Equal(TimeSpan.FromMinutes(9), Assert.Single(episodes, episode => episode.IsGap).Span);
        var resumed = episodes.Single(episode => episode.RecoveryWindows.Count > 0);
        Assert.Equal(boot.OccurredAt, resumed.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(90), resumed.RecoveryCoveredDuration);
        Assert.Equal(TimeSpan.Zero, resumed.SystemCoveredDuration);
        Assert.Empty(resumed.StateEvidence);
    }

    [Fact]
    public void CurrentRecoveryWindow_DoesNotExtendIntoTheFuture()
    {
        var now = _start.AddSeconds(650);
        var episode = Assert.Single(TimelinePresentation.Build([], _day, stateEvents: States(), observedUntil: now));

        Assert.Equal(now, episode.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(50), episode.RecoveryCoveredDuration);
    }

    [Fact]
    public void OverlappingResumeReports_DoNotDuplicateRecoveryCoverage()
    {
        var events = States().Concat(States(source: "system:recovered-sleep", eventId: 1)
            .Select(value => value with { Key = "recovered:" + value.Key })).ToArray();
        var episode = Assert.Single(TimelinePresentation.Build([], _day, stateEvents: events));

        Assert.Equal(TimeSpan.FromMinutes(10), episode.SystemCoveredDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), episode.RecoveryCoveredDuration);
        Assert.Equal(2, episode.RecoveryWindows.Count);
        Assert.Equal(TimeSpan.FromMinutes(12), episode.Span);
    }

    [Fact]
    public void MidnightInsideRecovery_PreservesAwayContinuationRatherThanFoldingIntoActiveUse()
    {
        var events = States().Select(value => value with { OccurredAt = value.OccurredAt.AddSeconds(-630) }).ToArray();
        var active = Record(1, 60, 660);
        var before = Assert.Single(TimelinePresentation.Build([], new DateRange(_start.AddDays(-1), _start), stateEvents: events));
        var after = TimelinePresentation.Build([active], _day, stateEvents: events);
        var away = Assert.Single(after, episode => episode.IsAfk);

        Assert.True(before.ContinuesAfterRange);
        Assert.True(away.ContinuesBeforeRange);
        Assert.Equal(_start, away.StartedAt);
        Assert.Equal(active.StartedAt, away.EndedAt);
        Assert.Empty(away.StateEvidence);
        Assert.Equal(TimeSpan.FromMinutes(1), away.RecoveryCoveredDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), after.Single(episode => !episode.IsAfk).ActiveDuration);
    }

    [Fact]
    public void PreviousDayFirstSample_DoesNotReopenRecoveryAfterMidnight()
    {
        var boot = new DeviceStateEvent("boot", _start.AddMinutes(-1), DeviceStateKind.Shutdown,
            false, "system:shutdown", "Windows 开机", 12);
        var firstSample = Record(1, -50, -40, true);
        var laterSample = Record(2, 180, 780, true);

        var episodes = TimelinePresentation.Build([firstSample, laterSample], _day, stateEvents: [boot]);

        Assert.All(episodes, episode => Assert.Empty(episode.RecoveryWindows));
        Assert.Equal(TimeSpan.Zero, TimeSpan.FromTicks(episodes.Sum(episode => episode.RecoveryCoveredDuration.Ticks)));
    }

    [Fact]
    public void Pagination_DoesNotBufferUnloadedSamples()
    {
        var afk = Record(1, 660, 680, true);
        var active = Record(2, 690, 1290);
        var partial = TimelinePresentation.Build([active], _day, true, States());
        var complete = TimelinePresentation.Build([afk, active], _day, false, States());

        Assert.Empty(Assert.Single(partial).RecoveryWindows);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, complete.Sum(episode => episode.RecoveryCoveredDuration.Ticks));
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, complete.Sum(episode => episode.ActiveDuration.Ticks));
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, complete.Where(episode => episode.IsAfk).Sum(episode => episode.ObservedDuration.Ticks));
    }

    private static DeviceStateEvent[] States(DeviceStateKind kind = DeviceStateKind.Sleep,
        string source = "system:sleep", int eventId = 107) =>
    [
        new("start", _start, kind, true, source, "系统状态", 42),
        new("end", _start.AddMinutes(10), kind, false, source, "系统状态", eventId)
    ];

    private static ActivitySegment Record(long id, int start, int end, bool afk = false) => new(id,
        _start.AddSeconds(start), _start.AddSeconds(end), "code", "IDE", "", afk, false, false, "", "#007AFF");
}
