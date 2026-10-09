using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.ViewModels;

namespace LiveQs.Windows.Tests;

public sealed class TimelinePresentationTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateRange Range = new(Day, Day.AddDays(1));

    [Fact]
    public void ContinuousFullscreenVideo_RemainsOneHourWithOneRecord()
    {
        var record = Segment(1, 0, 3600, "edge", "视频页面") with { IsFullscreen = true };
        var episode = Assert.Single(TimelinePresentation.Build([record], Range));

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
        var episode = Assert.Single(TimelinePresentation.Build(records.Reverse(), Range));

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
        var episode = Assert.Single(TimelinePresentation.Build(records, Range));

        Assert.Equal(90, episode.Slices.Count);
        Assert.Equal(89, episode.TitleChanges);
        Assert.Equal(0, episode.ApplicationSwitches);
        Assert.Equal(TimeSpan.FromMinutes(90), episode.ActiveDuration);
    }

    [Fact]
    public void Afk_IsItsOwnEpisodeAndNeverCountsAsActive()
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 600), Segment(2, 600, 1200) with { IsAfk = true }, Segment(3, 1200, 1800)
        ], Range);

        Assert.Equal(3, episodes.Count);
        Assert.True(episodes[1].IsAfk);
        Assert.Equal(TimeSpan.Zero, episodes[1].ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), episodes[1].ObservedDuration);
        Assert.Equal(20, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Fact]
    public void MissingRecords_AreAVisibleGapRatherThanAfkOrUsage()
    {
        var episodes = TimelinePresentation.Build([Segment(1, 0, 120), Segment(2, 300, 420)], Range);
        var gap = Assert.Single(episodes, episode => episode.IsGap);

        Assert.Equal(TimeSpan.FromMinutes(3), gap.Span);
        Assert.False(gap.IsAfk);
        Assert.Equal(TimeSpan.Zero, gap.ActiveDuration);
        Assert.Equal(4, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
    }

    [Fact]
    public void ShortGaps_StayBlankWithinAnEpisodeAndDoNotIncreaseDuration()
    {
        var episode = Assert.Single(TimelinePresentation.Build([Segment(1, 0, 60), Segment(2, 70, 130)], Range));

        Assert.Equal(TimeSpan.FromSeconds(130), episode.Span);
        Assert.Equal(TimeSpan.FromSeconds(120), episode.ObservedDuration);
        Assert.Equal(Day.AddSeconds(60), episode.Slices[0].EndedAt);
        Assert.Equal(Day.AddSeconds(70), episode.Slices[1].StartedAt);
    }

    [Fact]
    public void QueryClipping_PreservesTheOriginalRecord()
    {
        var record = Segment(1, -600, 600);
        var episode = Assert.Single(TimelinePresentation.Build([record], Range));

        Assert.Equal(Day, episode.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), episode.ActiveDuration);
        Assert.Same(record, episode.Slices[0].Record);
        Assert.Equal(Day.AddMinutes(-10), episode.Slices[0].Record.StartedAt);
        Assert.Empty(TimelinePresentation.Build([Segment(2, -120, -60), Segment(3, 86400, 86500)], Range));
    }

    [Fact]
    public void ForegroundOverlap_UsesTheLaterStartWithoutDoubleCountingOrRewriting()
    {
        var first = Segment(1, 0, 600, "code");
        var second = Segment(2, 300, 1200, "edge");
        var episodes = TimelinePresentation.Build([first, second], Range);

        Assert.Equal(20, episodes.Sum(episode => episode.ActiveDuration.TotalMinutes));
        var slice = Assert.Single(episodes.SelectMany(episode => episode.Slices), slice => slice.Record.Id == 1);
        Assert.Equal(Day.AddMinutes(5), slice.EndedAt);
        Assert.Equal(Day.AddMinutes(10), slice.Record.EndedAt);
    }

    [Fact]
    public void SustainedDifferentApplication_StartsItsOwnEpisode()
    {
        var episodes = TimelinePresentation.Build([
            Segment(1, 0, 60, "code"), Segment(2, 60, 660, "edge"), Segment(3, 660, 720, "code")
        ], Range);

        Assert.Equal(3, episodes.Count);
        Assert.Equal("edge", Assert.Single(episodes[1].Applications).AppId);
    }

    [Fact]
    public void MixedActivity_DoesNotChainTogetherAnEntireDay()
    {
        var records = Enumerable.Range(0, 120).Select(index =>
            Segment(index + 1, index * 60, (index + 1) * 60, index % 2 == 0 ? "code" : "edge"));
        var episodes = TimelinePresentation.Build(records, Range);

        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, episode => Assert.Equal(TimeSpan.FromHours(1), episode.Span));
        Assert.Equal(120, episodes.Sum(episode => episode.Slices.Count));
    }

    [Fact]
    public void EarlierPages_ExtendTheSameEpisodeAndRemoveItsPartialMarker()
    {
        var records = DevelopmentRecords();
        var partial = Assert.Single(TimelinePresentation.Build(records.Skip(42), Range, hasEarlierRecords: true));
        var complete = Assert.Single(TimelinePresentation.Build(records.Skip(42).Concat(records.Take(42)), Range));

        Assert.True(partial.IsPartial);
        Assert.False(complete.IsPartial);
        Assert.Equal(84, complete.Slices.Count);
        Assert.Equal(TimeSpan.FromHours(1), complete.ActiveDuration);
    }

    [Fact]
    public void TimeSelection_OpensTheMatchingRawRecordsAndKeepsTheirTitles()
    {
        var row = new TimelineEpisodeViewModel(Assert.Single(TimelinePresentation.Build(DevelopmentRecords(), Range)));
        row.SelectPositionCommand.Execute(0.1d);

        Assert.True(row.IsExpanded);
        Assert.True(row.IsRawExpanded);
        Assert.Equal(Day.AddMinutes(5), row.SelectedWindow!.StartedAt);
        Assert.Equal(7, row.RawRecords.Count);
        Assert.Contains(row.RawRecords, record => record.Title == "localhost:5173");
        Assert.Contains(row.RawRecords, record => record.Duration == "1分钟 20秒");
        Assert.Equal(1d / 12, row.SelectionStart, 10);
        Assert.Equal(1d / 6, row.SelectionEnd, 10);
        row.SelectPositionCommand.Execute(1d);
        Assert.Equal(Day.AddMinutes(55), row.SelectedWindow!.StartedAt);
    }

    [Fact]
    public void OverlappingPageRecordsAndInvalidIntervals_DoNotDuplicateTheProjection()
    {
        var record = Segment(1, 0, 60);
        var episode = Assert.Single(TimelinePresentation.Build([record, record, Segment(2, 60, 60)], Range));

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
        new(id, Day.AddSeconds(start), Day.AddSeconds(end), app, app, title, false, false, false, "未分类", "#007AFF");
}
