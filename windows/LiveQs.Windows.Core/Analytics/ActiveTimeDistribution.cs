using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Analytics;

public static class ActiveTimeDistribution
{
    public static IReadOnlyList<ActiveTimeBucket> Build(
        DateRange range, IReadOnlyList<ActivitySegment> segments, TimeZoneInfo timeZone) =>
        BuildFromSlices(range, ActivityIntervals.CreateSlices(segments, range), timeZone);

    internal static IReadOnlyList<ActiveTimeBucket> BuildFromSlices(
        DateRange range, IReadOnlyList<ActivitySlice> activitySlices, TimeZoneInfo timeZone)
    {
        if (range.End <= range.Start)
        {
            return [];
        }

        var firstDate = TimeZoneInfo.ConvertTime(range.Start, timeZone).Date;
        var lastDate = TimeZoneInfo.ConvertTime(range.End.AddTicks(-1), timeZone).Date;
        var hourly = firstDate == lastDate;
        var count = hourly ? 24 : (lastDate - firstDate).Days + 1;
        var periods = new long[count];
        var active = new long[count];
        var slices = new List<(DateTimeOffset Start, DateTimeOffset End, int Index)>();

        for (var cursor = range.Start; cursor < range.End;)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, timeZone);
            var next = hourly ? NextHourBoundary(cursor, timeZone) : Midnight(local.Date.AddDays(1), timeZone);
            if (next > range.End)
            {
                next = range.End;
            }

            var index = hourly ? local.Hour : (local.Date - firstDate).Days;
            periods[index] += (next - cursor).Ticks;
            slices.Add((cursor, next, index));
            cursor = next;
        }

        foreach (var slice in activitySlices)
        {
            if (slice.Record.IsAfk || slice.Duration <= TimeSpan.Zero)
            {
                continue;
            }
            // Find the first intersecting slice, avoiding a full range scan per segment.
            var low = 0;
            var high = slices.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (slices[middle].End <= slice.StartedAt)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            for (var index = low; index < slices.Count && slices[index].Start < slice.EndedAt; index++)
            {
                var period = slices[index];
                var start = slice.StartedAt > period.Start ? slice.StartedAt : period.Start;
                var end = slice.EndedAt < period.End ? slice.EndedAt : period.End;
                if (end > start)
                {
                    active[period.Index] += (end - start).Ticks;
                }
            }
        }

        return Enumerable.Range(0, count).Select(index => new ActiveTimeBucket(
            hourly ? firstDate.AddHours(index) : firstDate.AddDays(index),
            TimeSpan.FromTicks(periods[index]), TimeSpan.FromTicks(active[index]))).ToArray();
    }

    private static DateTimeOffset Midnight(DateTime date, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static DateTimeOffset NextHourBoundary(DateTimeOffset cursor, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(cursor, timeZone);
        var next = cursor.AddTicks(TimeSpan.TicksPerHour - local.TimeOfDay.Ticks % TimeSpan.TicksPerHour);
        if (TimeZoneInfo.ConvertTime(next.AddTicks(-1), timeZone).Offset == local.Offset)
        {
            return next;
        }

        // Split at an offset transition as well, including half-hour DST changes.
        var low = cursor.UtcTicks;
        var high = next.UtcTicks;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (TimeZoneInfo.ConvertTime(new DateTimeOffset(middle, TimeSpan.Zero), timeZone).Offset == local.Offset)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return new DateTimeOffset(high, TimeSpan.Zero);
    }
}
