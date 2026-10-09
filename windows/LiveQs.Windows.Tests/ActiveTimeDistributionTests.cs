using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Tests;

public sealed class ActiveTimeDistributionTests
{
    [Fact]
    public void Hourly_SplitsIntervalsAndExcludesAfkOutsideTheRange()
    {
        var day = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        var range = new DateRange(day, day.AddDays(1));
        var segments = new[]
        {
            Segment(day.AddMinutes(-5), day.AddMinutes(10)),
            Segment(day.AddHours(9).AddMinutes(45), day.AddHours(10).AddMinutes(15)),
            Segment(day.AddHours(10).AddMinutes(15), day.AddHours(11), afk: true),
            Segment(day.AddHours(23).AddMinutes(50), day.AddDays(1).AddMinutes(5)),
        };

        var buckets = ActiveTimeDistribution.Build(range, segments, TimeZoneInfo.Utc);

        Assert.Equal(24, buckets.Count);
        Assert.Equal(Enumerable.Range(0, 24), buckets.Select(bucket => bucket.LocalStart.Hour));
        Assert.Equal(10, buckets[0].ActiveDuration.TotalMinutes);
        Assert.Equal(15, buckets[9].ActiveDuration.TotalMinutes);
        Assert.Equal(15, buckets[10].ActiveDuration.TotalMinutes);
        Assert.Equal(0.25, buckets[10].Share);
        Assert.Equal(TimeSpan.Zero, buckets[11].ActiveDuration);
        Assert.Equal(10, buckets[23].ActiveDuration.TotalMinutes);
        Assert.Equal(50, buckets.Sum(bucket => bucket.ActiveDuration.TotalMinutes));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(40)]
    public void Daily_SplitsAtMidnightAndRetainsEveryDate(int days)
    {
        var first = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var range = new DateRange(first, first.AddDays(days));
        var segments = new[] { Segment(first.AddHours(23), first.AddDays(1).AddHours(2)) };

        var buckets = ActiveTimeDistribution.Build(range, segments, TimeZoneInfo.Utc);

        Assert.Equal(days, buckets.Count);
        Assert.Equal(first.Date, buckets[0].LocalStart);
        Assert.Equal(first.AddDays(days - 1).Date, buckets[^1].LocalStart);
        Assert.Equal(TimeSpan.FromHours(1), buckets[0].ActiveDuration);
        Assert.Equal(TimeSpan.FromHours(2), buckets[1].ActiveDuration);
        Assert.Equal(2d / 24, buckets[1].Share, 10);
        Assert.All(buckets.Skip(2), bucket => Assert.Equal(TimeSpan.Zero, bucket.ActiveDuration));
        Assert.Equal(3, buckets.Sum(bucket => bucket.ActiveDuration.TotalHours));
    }

    [Theory]
    [InlineData("Eastern Standard Time", 2026, 3, 8, 2, 0)]
    [InlineData("Eastern Standard Time", 2026, 11, 1, 1, 120)]
    [InlineData("Australia/Lord_Howe", 2026, 10, 4, 2, 30)]
    [InlineData("Australia/Lord_Howe", 2026, 4, 5, 1, 90)]
    public void Hourly_Preserves24LabelsAcrossOffsetChanges(
        string zoneId, int year, int month, int day, int hour, int expectedMinutes)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var date = new DateTime(year, month, day);
        var range = Range(date, date.AddDays(1), zone);
        var buckets = ActiveTimeDistribution.Build(range, [Segment(range.Start, range.End)], zone);

        Assert.Equal(24, buckets.Count);
        Assert.Equal(expectedMinutes, buckets[hour].PeriodDuration.TotalMinutes);
        Assert.Equal(expectedMinutes, buckets[hour].ActiveDuration.TotalMinutes);
        Assert.Equal(range.End - range.Start, TimeSpan.FromTicks(buckets.Sum(bucket => bucket.ActiveDuration.Ticks)));
        Assert.Equal(expectedMinutes == 0 ? 0 : 1, buckets[hour].Share);
    }

    [Fact]
    public void Daily_UsesLocalDatesAndActualDayLength()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var range = Range(new DateTime(2026, 3, 7), new DateTime(2026, 3, 10), zone);
        var buckets = ActiveTimeDistribution.Build(range, [Segment(range.Start, range.End)], zone);

        Assert.Equal(new[] { 24d, 23d, 24d }, buckets.Select(bucket => bucket.PeriodDuration.TotalHours));
        Assert.All(buckets, bucket => Assert.Equal(1, bucket.Share));
    }

    [Fact]
    public void EmptyDay_StillHas24ZeroBuckets()
    {
        var day = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        var buckets = ActiveTimeDistribution.Build(new DateRange(day, day.AddDays(1)), [], TimeZoneInfo.Utc);

        Assert.Equal(24, buckets.Count);
        Assert.All(buckets, bucket => Assert.Equal(0, bucket.Share));
    }

    private static DateRange Range(DateTime start, DateTime end, TimeZoneInfo zone) => new(
        new DateTimeOffset(start, zone.GetUtcOffset(start)), new DateTimeOffset(end, zone.GetUtcOffset(end)));

    private static ActivitySegment Segment(DateTimeOffset start, DateTimeOffset end, bool afk = false) =>
        new(start.UtcTicks, start, end, "editor.exe", "Editor", "", afk, false, false, "工作", "#007AFF");
}
