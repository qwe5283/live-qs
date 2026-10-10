using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Tests;

public sealed class TimelinePresentationTests
{
    private static readonly DateTimeOffset _day = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateRange _range = new(_day, _day.AddDays(1));

    [Fact]
    public void ContinuousFullscreenVideo_RemainsOneHourWithOneRecord()
    {
        var record = Segment(1, 0, 3600, "edge", "视频页面") with
        {
            IsFullscreen = true
        };
        var episode = Assert.Single(TimelinePresentation.Build([record], _range));

        Assert.Equal(TimeSpan.FromHours(1), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromHours(1), episode.Span);
        Assert.Same(record, Assert.Single(episode.Slices).Record);
        Assert.Equal(0, episode.ApplicationSwitches);
        Assert.Equal(0, episode.TitleChanges);
    }

    [Fact]
    public void RapidDevelopmentSwitching_ProducesOneEpisodeWithCorrectAllocationAndChanges()
    {
        var records = DevelopmentRecords();
        var episode = Assert.Single(TimelinePresentation.Build(records.Reverse(), _range));

        Assert.Equal(84, episode.Slices.Count);
        Assert.Equal(48, episode.ApplicationSwitches);
        Assert.Equal(35, episode.TitleChanges);
        Assert.Equal(TimeSpan.FromHours(1), episode.ActiveDuration);
        Assert.Equal(new[] { "code", "edge", "terminal", "explorer" }, episode.Applications.Select(application => application.AppId));
        Assert.Equal(new[] { 30d, 18d, 9d, 3d }, episode.Applications.Select(application => application.Duration.TotalMinutes));
        Assert.Equal(records.Select(record => record.Id), episode.Slices.Select(slice => slice.Record.Id));
    }

    [Fact]
    public void ConsecutiveSameApplicationTitles_AreFoldedAcrossHourBoundaries()
    {
        var records = Enumerable.Range(0, 90).Select(index =>
            Segment(index + 1, index * 60, (index + 1) * 60, index % 2 == 0 ? "CODE" : "code", $"file-{index}.ts")).ToArray();
        var episode = Assert.Single(TimelinePresentation.Build(records, _range));

        Assert.Equal(90, episode.Slices.Count);
        Assert.Equal(89, episode.TitleChanges);
        Assert.Equal(0, episode.ApplicationSwitches);
        Assert.Equal(TimeSpan.FromMinutes(90), episode.ActiveDuration);
    }

    [Fact]
    public void LongAfk_IsItsOwnEpisodeAndNeverCountsAsActive()
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 600), Segment(2, 600, 1200) with { IsAfk = true }, Segment(3, 1200, 1800)
        ], _range);

        Assert.Equal(3, episodes.Count);
        Assert.True(episodes[1].IsAfk);
        Assert.Equal(TimeSpan.Zero, episodes[1].ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), episodes[1].ObservedDuration);
        Assert.Equal(20, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(180)]
    [InlineData(300)]
    public void ShortAfk_BridgesSustainedSameApplicationAndStaysBlank(int seconds)
    {
        var afk = Segment(2, 600, 600 + seconds, "lock", "锁屏") with
        {
            IsAfk = true
        };
        var episode = Assert.Single(TimelinePresentation.Build([
            Segment(1, 0, 600, "code", "main.ts"), afk,
            Segment(3, 600 + seconds, 1200 + seconds, "CODE", "Player.vue")
        ], _range));

        Assert.False(episode.IsAfk);
        Assert.Equal(TimeSpan.FromSeconds(1200 + seconds), episode.Span);
        Assert.Equal(episode.Span, episode.ObservedDuration);
        Assert.Equal(TimeSpan.FromMinutes(20), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(20), Assert.Single(episode.Applications).Duration);
        Assert.Equal("code", episode.Applications[0].AppId, ignoreCase: true);
        Assert.Equal(0, episode.ApplicationSwitches);
        Assert.Equal(1, episode.TitleChanges);
        Assert.Same(afk, episode.Slices[1].Record);

        var row = new TimelineEpisodeViewModel(episode);
        Assert.Equal(2, row.Marks.Count);
        Assert.Equal(600d / (1200 + seconds), row.Marks[0].End, 10);
        Assert.Equal((600d + seconds) / (1200 + seconds), row.Marks[1].Start, 10);
        Assert.Equal(row.Marks.Select(mark => (mark.Start, mark.End)),
            Assert.Single(row.Applications).Marks.Select(mark => (mark.Start, mark.End)));
        Assert.True(row.HasCoverageDifference);
        Assert.Contains("AFK", row.Coverage);
        row.SelectPositionCommand.Execute((600d + seconds / 2d) / (1200 + seconds));
        Assert.Contains(row.RawRecords, record => record.Id == afk.Id && record.Flags.Contains("AFK"));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(600)]
    public void AfkOverFiveMinutes_SeparatesTheSurroundingActivity(int seconds)
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 600), Segment(2, 600, 600 + seconds) with { IsAfk = true },
            Segment(3, 600 + seconds, 1200 + seconds)
        ], _range);

        Assert.Equal(3, episodes.Count);
        Assert.True(episodes[1].IsAfk);
        Assert.Equal(TimeSpan.FromSeconds(seconds), episodes[1].ObservedDuration);
        Assert.Equal(TimeSpan.Zero, episodes[1].ActiveDuration);
        Assert.Equal(20, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Fact]
    public void ShortAfk_StaysWithinMixedActivityWithoutInflatingApplicationsOrChanges()
    {
        var episode = Assert.Single(TimelinePresentation.Build([
            Segment(1, 0, 120, "code"), Segment(2, 120, 180, "edge"),
            Segment(3, 180, 480, "lock", "锁屏") with { IsAfk = true },
            Segment(4, 480, 540, "terminal"), Segment(5, 540, 660, "code")
        ], _range));

        Assert.Equal(TimeSpan.FromMinutes(11), episode.ObservedDuration);
        Assert.Equal(TimeSpan.FromMinutes(6), episode.ActiveDuration);
        Assert.Equal(new[] { "code", "edge", "terminal" }, episode.Applications.Select(application => application.AppId));
        Assert.Equal(new[] { 4d, 1d, 1d }, episode.Applications.Select(application => application.Duration.TotalMinutes));
        Assert.Equal(3, episode.ApplicationSwitches);
        Assert.Equal(0, episode.TitleChanges);
        var row = new TimelineEpisodeViewModel(episode);
        Assert.Equal("多应用活动", row.Title);
        Assert.Equal(4, row.Marks.Count);
        Assert.All(row.Applications.SelectMany(application => application.Marks), mark =>
            Assert.True(mark.End <= 180d / 660 || mark.Start >= 480d / 660));
    }

    [Theory]
    [InlineData(150, 1)]
    [InlineData(151, 3)]
    public void ConsecutiveAfkRecords_UseTheirCombinedDuration(int lastSeconds, int expectedEpisodes)
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 600), Segment(2, 600, 750, "lock") with { IsAfk = true },
            Segment(3, 750, 750 + lastSeconds, "screen") with { IsAfk = true },
            Segment(4, 750 + lastSeconds, 1350 + lastSeconds)
        ], _range);

        Assert.Equal(expectedEpisodes, episodes.Count);
        Assert.Equal(4, episodes.Sum(episode => episode.Slices.Count));
        Assert.Equal(TimeSpan.FromMinutes(20), TimeSpan.FromTicks(episodes.Sum(episode => episode.ActiveDuration.Ticks)));
    }

    [Fact]
    public void AfkDuration_UsesItsSpanIncludingShortUnrecordedGaps()
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 600), Segment(2, 600, 740) with { IsAfk = true },
            Segment(3, 770, 910) with { IsAfk = true }, Segment(4, 910, 1510)
        ], _range);

        Assert.Equal(3, episodes.Count);
        Assert.True(episodes[1].IsAfk);
        Assert.Equal(TimeSpan.FromSeconds(310), episodes[1].Span);
        Assert.Equal(TimeSpan.FromSeconds(280), episodes[1].ObservedDuration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShortAfk_DoesNotBridgeAnUnrecordedGapOverThirtySeconds(bool gapBeforeAfk)
    {
        var afkStart = gapBeforeAfk ? 91 : 60;
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 60), Segment(2, afkStart, afkStart + 120) with { IsAfk = true },
            Segment(3, 211, 271)
        ], _range);

        Assert.Equal(3, episodes.Count);
        Assert.Equal(TimeSpan.FromSeconds(31), Assert.Single(episodes, episode => episode.IsGap).Span);
        Assert.Equal(2, episodes.Count(episode => !episode.IsAfk && !episode.IsGap));
        Assert.Equal(2, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShortAfk_AtTheQueryEdgeJoinsTheOnlyAdjacentActivity(bool afkFirst)
    {
        var episodes = TimelinePresentation.Build(afkFirst
            ? [Segment(1, 0, 180) with { IsAfk = true }, Segment(2, 180, 780)]
            : [Segment(1, 0, 600), Segment(2, 600, 780) with { IsAfk = true }], _range);

        var episode = Assert.Single(episodes);
        Assert.False(episode.IsAfk);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(13), episode.ObservedDuration);
    }

    [Fact]
    public void AfkWithoutAdjacentActivity_RemainsVisibleWithItsRawRecord()
    {
        var afk = Segment(1, 0, 300) with
        {
            IsAfk = true
        };
        var episode = Assert.Single(TimelinePresentation.Build([afk], _range));
        var row = new TimelineEpisodeViewModel(episode);

        Assert.True(episode.IsAfk);
        Assert.False(episode.IsGap);
        Assert.Equal(TimeSpan.Zero, episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), episode.ObservedDuration);
        Assert.Empty(row.Marks);
        Assert.Empty(row.Applications);
        Assert.False(row.HasChanges);
        row.SelectPositionCommand.Execute(0.5d);
        Assert.Equal(afk.Id, Assert.Single(row.RawRecords).Id);
    }

    [Fact]
    public void ShortAfk_PreservesTheBoundaryOfDifferentSustainedApplications()
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 600, "code"), Segment(2, 600, 780) with { IsAfk = true },
            Segment(3, 780, 1380, "edge")
        ], _range);

        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, episode => Assert.False(episode.IsAfk));
        Assert.Equal(new[] { "edge", "code" }, episodes.Select(episode => Assert.Single(episode.Applications).AppId));
        Assert.Equal(20, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Fact]
    public void RepeatedShortAfk_KeepsLongSameApplicationActivityTogether()
    {
        var episode = Assert.Single(TimelinePresentation.Build([
            Segment(1, 0, 2400), Segment(2, 2400, 2580) with { IsAfk = true },
            Segment(3, 2580, 4980), Segment(4, 4980, 5160) with { IsAfk = true },
            Segment(5, 5160, 7560)
        ], _range));

        Assert.Equal(TimeSpan.FromHours(2), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(126), episode.Span);
        Assert.Equal(3, new TimelineEpisodeViewModel(episode).Marks.Count);
    }

    [Fact]
    public void EarlierPages_MergeAcrossShortAfkAndPreserveTheBreak()
    {
        var later = new[] { Segment(2, 600, 900) with { IsAfk = true }, Segment(3, 900, 1500) };
        var partial = Assert.Single(TimelinePresentation.Build(later, _range, hasEarlierRecords: true));
        var complete = Assert.Single(TimelinePresentation.Build(later.Append(Segment(1, 0, 600)), _range));

        Assert.True(partial.IsPartial);
        Assert.False(complete.IsPartial);
        Assert.Equal(TimeSpan.FromMinutes(20), complete.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(25), complete.Span);
        Assert.Equal(new[] { 1L, 2L, 3L }, complete.Slices.Select(slice => slice.Record.Id));
        var row = new TimelineEpisodeViewModel(complete);
        Assert.Equal(0.4d, row.Marks[0].End, 10);
        Assert.Equal(0.6d, row.Marks[1].Start, 10);
    }

    [Fact]
    public void MissingRecords_AreAVisibleGapRatherThanAfkOrUsage()
    {
        var episodes = TimelinePresentation.Build([Segment(1, 0, 120), Segment(2, 300, 420)], _range);
        var gap = Assert.Single(episodes, episode => episode.IsGap);

        Assert.Equal(TimeSpan.FromMinutes(3), gap.Span);
        Assert.False(gap.IsAfk);
        Assert.Equal(TimeSpan.Zero, gap.ActiveDuration);
        Assert.Equal(4, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Fact]
    public void ShortGaps_StayBlankWithinAnEpisodeAndDoNotIncreaseDuration()
    {
        var episode = Assert.Single(TimelinePresentation.Build([Segment(1, 0, 60), Segment(2, 70, 130)], _range));

        Assert.Equal(TimeSpan.FromSeconds(130), episode.Span);
        Assert.Equal(TimeSpan.FromSeconds(120), episode.ObservedDuration);
        Assert.Equal(_day.AddSeconds(60), episode.Slices[0].EndedAt);
        Assert.Equal(_day.AddSeconds(70), episode.Slices[1].StartedAt);
    }

    [Fact]
    public void QueryClipping_PreservesTheOriginalRecord()
    {
        var record = Segment(1, -600, 600);
        var episode = Assert.Single(TimelinePresentation.Build([record], _range));

        Assert.Equal(_day, episode.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ActiveDuration);
        Assert.Same(record, episode.Slices[0].Record);
        Assert.Equal(_day.AddMinutes(-10), episode.Slices[0].Record.StartedAt);
        Assert.Empty(TimelinePresentation.Build([Segment(2, -120, -60), Segment(3, 86400, 86500)], _range));
    }

    [Fact]
    public void ForegroundOverlap_UsesTheLaterStartWithoutDoubleCountingOrRewriting()
    {
        var first = Segment(1, 0, 600, "code");
        var second = Segment(2, 300, 1200, "edge");
        var episodes = TimelinePresentation.Build([first, second], _range);

        Assert.Equal(20, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
        var slice = Assert.Single(episodes.SelectMany(episode => episode.Slices), slice => slice.Record.Id == 1);
        Assert.Equal(_day.AddMinutes(5), slice.EndedAt);
        Assert.Equal(_day.AddMinutes(10), slice.Record.EndedAt);
    }

    [Fact]
    public void SupersededAfkTail_DoesNotSplitAShortBreak()
    {
        var afk = Segment(1, 0, 600) with
        {
            IsAfk = true
        };
        var active = Segment(2, 120, 720);

        var episode = Assert.Single(TimelinePresentation.Build([afk, active], _range));

        Assert.False(episode.IsAfk);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(12), episode.ObservedDuration);
        Assert.Equal(_day.AddMinutes(10), afk.EndedAt);
    }

    [Theory]
    [InlineData(-300, 120)]
    [InlineData(86300, 86360)]
    public void SupersededAfkTail_DoesNotCreateFalseCrossDayContinuation(int afkStart, int activeStart)
    {
        var afk = Segment(1, afkStart, 87000) with
        {
            IsAfk = true
        };
        var active = Segment(2, activeStart, 86400);

        var episodes = TimelinePresentation.Build([afk, active], _range);

        Assert.All(episodes, episode => Assert.False(episode.ContinuesAfterRange));
        if (afkStart < 0)
        {
            Assert.True(Assert.Single(episodes, episode => episode.IsAfk).ContinuesBeforeRange);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void SameStartRecords_RemainInspectableWithoutDoubleCounting(int start)
    {
        var original = Segment(1, start, start + 600, title: "old title");
        var newer = Segment(2, start, start + 600, title: "new title");

        var episode = Assert.Single(TimelinePresentation.Build([original, newer], _range));
        var row = new TimelineEpisodeViewModel(episode);
        row.SelectPositionCommand.Execute(0.1d);

        Assert.Equal(2, episode.RecordCount);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ObservedDuration);
        Assert.Contains(row.RawRecords, record => record.Id == original.Id && record.Title == "old title");
        Assert.Contains(row.RawRecords, record => record.Id == newer.Id && record.Title == "new title");
        Assert.Equal(0, episode.TitleChanges);
    }

    [Fact]
    public void SupersededEstimateBeforeQueryStart_RemainsOutsideTheProjection()
    {
        var records = new[] { Segment(1, -2, 8), Segment(2, -1, 0, "edge") };

        var episodes = TimelinePresentation.Build(records, _range);

        Assert.Empty(episodes);
        Assert.Equal(_day.AddSeconds(8), records[0].EndedAt);
    }

    [Fact]
    public void SustainedDifferentApplication_StartsItsOwnEpisode()
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 60, "code"), Segment(2, 60, 660, "edge"), Segment(3, 660, 720, "code")
        ], _range);

        Assert.Equal(3, episodes.Count);
        Assert.Equal("edge", Assert.Single(episodes[1].Applications).AppId);
    }

    [Fact]
    public void MixedActivity_DoesNotChainTogetherAnEntireDay()
    {
        var records = Enumerable.Range(0, 120).Select(index =>
            Segment(index + 1, index * 60, (index + 1) * 60, index % 2 == 0 ? "code" : "edge"));
        var episodes = TimelinePresentation.Build(records, _range);

        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, episode => Assert.Equal(TimeSpan.FromHours(1), episode.Span));
        Assert.Equal(120, episodes.Sum(episode => episode.Slices.Count));
    }

    [Fact]
    public void EarlierPages_ExtendTheSameEpisodeAndRemoveItsPartialMarker()
    {
        var records = DevelopmentRecords();
        var partial = Assert.Single(TimelinePresentation.Build(records.Skip(42), _range, hasEarlierRecords: true));
        var complete = Assert.Single(TimelinePresentation.Build(records.Skip(42).Concat(records.Take(42)), _range));

        Assert.True(partial.IsPartial);
        Assert.False(complete.IsPartial);
        Assert.Equal(84, complete.Slices.Count);
        Assert.Equal(TimeSpan.FromHours(1), complete.ActiveDuration);
    }

    [Fact]
    public void TimeSelection_OpensTheMatchingRawRecordsAndKeepsTheirTitles()
    {
        var row = new TimelineEpisodeViewModel(Assert.Single(TimelinePresentation.Build(DevelopmentRecords(), _range)));
        row.SelectPositionCommand.Execute(0.1d);

        Assert.True(row.IsExpanded);
        Assert.True(row.IsRawExpanded);
        Assert.Equal(_day.AddMinutes(5), row.SelectedWindow!.StartedAt);
        Assert.Equal(7, row.RawRecords.Count);
        Assert.Contains(row.RawRecords, record => record.Title == "localhost:5173");
        Assert.Contains(row.RawRecords, record => record.Duration == "1分钟 20秒");
        Assert.Equal(1d / 12, row.SelectionStart, 10);
        Assert.Equal(1d / 6, row.SelectionEnd, 10);
        row.SelectPositionCommand.Execute(1d);
        Assert.Equal(_day.AddMinutes(55), row.SelectedWindow!.StartedAt);
    }

    [Fact]
    public void OverlappingPageRecordsAndInvalidIntervals_DoNotDuplicateTheProjection()
    {
        var record = Segment(1, 0, 60);
        var episode = Assert.Single(TimelinePresentation.Build([record, record, Segment(2, 60, 60)], _range));

        Assert.Single(episode.Slices);
        Assert.Equal(TimeSpan.FromMinutes(1), episode.ActiveDuration);
    }

    internal static ActivitySegment[] DevelopmentRecords()
    {
        var parts = new (string App, int Seconds, string Title)[]
        {
            ("code", 80, "main.ts"), ("code", 40, "Player.vue"), ("terminal", 45, "npm run dev"),
            ("edge", 60, "localhost:5173"), ("edge", 30, "MDN Web Docs"), ("explorer", 15, "demo-project"), ("code", 30, "Player.test.ts"),
        };
        var records = new List<ActivitySegment>();
        var seconds = 0;
        for (var cycle = 0; cycle < 12; cycle++)
        {
            foreach (var part in parts)
            {
                records.Add(Segment(records.Count + 1, seconds, seconds + part.Seconds, part.App, part.Title));
                seconds += part.Seconds;
            }
        }
        return records.ToArray();
    }

    private static ActivitySegment Segment(long id, int start, int end, string app = "code", string title = "") =>
        new(id, _day.AddSeconds(start), _day.AddSeconds(end), app, app, title, false, false, false, "未分类", "#007AFF");
}
