using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Activity;

public sealed record ActivitySlice(ActivitySegment Record, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}

public static class ActivityIntervals
{
    public static IReadOnlyList<ActivitySlice> CreateSlices(IEnumerable<ActivitySegment> records, DateRange range)
    {
        var ordered = records
            .Where(record => record.EndedAt > record.StartedAt && record.StartedAt < range.End)
            .DistinctBy(record => record.Id)
            .OrderBy(record => record.StartedAt)
            .ThenBy(record => record.Id)
            .ToArray();
        var slices = new List<ActivitySlice>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var record = ordered[index];
            var end = record.EndedAt < range.End ? record.EndedAt : range.End;
            // A later foreground sample supersedes the earlier estimate; keep the raw record intact.
            if (index + 1 < ordered.Length && ordered[index + 1].StartedAt < end)
            {
                end = ordered[index + 1].StartedAt;
            }
            // Resolve supersession before range clipping, even when the newer sample ended before this query.
            if (end <= range.Start)
            {
                continue;
            }

            var start = record.StartedAt > range.Start ? record.StartedAt : range.Start;
            slices.Add(new ActivitySlice(record, start, end > start ? end : start));
        }
        return slices;
    }
}
