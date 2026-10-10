using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Activity;

public sealed record ActivitySlice(ActivitySegment Record, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
{
    public TimeSpan Duration => EndedAt - StartedAt;
    public DateTimeOffset EffectiveStartedAt { get; init; } = StartedAt;
    public DateTimeOffset EffectiveEndedAt { get; init; } = EndedAt;
    public bool SystemInactive
    {
        get; init;
    }
    public bool IsAfk => Record.IsAfk || SystemInactive || WindowsActivityState.IsLockScreen(Record.AppId);
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
            var effectiveEnd = record.EndedAt;
            // A later foreground sample supersedes the earlier estimate; keep the raw record intact.
            if (index + 1 < ordered.Length && ordered[index + 1].StartedAt < effectiveEnd)
            {
                effectiveEnd = ordered[index + 1].StartedAt;
            }
            // Resolve supersession before range clipping, even when the newer sample ended before this query.
            if (effectiveEnd <= range.Start)
            {
                continue;
            }

            var start = record.StartedAt > range.Start ? record.StartedAt : range.Start;
            var end = effectiveEnd < range.End ? effectiveEnd : range.End;
            slices.Add(new ActivitySlice(record, start, end > start ? end : start)
            {
                EffectiveStartedAt = record.StartedAt,
                EffectiveEndedAt = effectiveEnd
            });
        }
        return slices;
    }

    public static IReadOnlyList<ActivitySlice> ApplyDeviceStates(
        IReadOnlyList<ActivitySlice> slices, IReadOnlyList<DeviceStateEvent> stateEvents,
        IReadOnlyList<DateRange>? deletedStateRanges = null)
    {
        var intervals = DeviceStateTimeline.Pair(stateEvents, deletedStateRanges);
        if (intervals.Count == 0)
        {
            return slices;
        }
        var result = new List<ActivitySlice>();
        foreach (var slice in slices)
        {
            var evidence = intervals.Where(value => value.StartedAt < slice.EndedAt && value.EndedAt > slice.StartedAt).ToArray();
            var boundaries = evidence.SelectMany(value => new[]
                {
                    value.StartedAt > slice.StartedAt ? value.StartedAt : slice.StartedAt,
                    value.EndedAt < slice.EndedAt ? value.EndedAt : slice.EndedAt
                })
                .Append(slice.StartedAt).Append(slice.EndedAt).Distinct().Order().ToArray();
            for (var index = 0; index + 1 < boundaries.Length; index++)
            {
                result.Add(slice with
                {
                    StartedAt = boundaries[index],
                    EndedAt = boundaries[index + 1],
                    SystemInactive = evidence.Any(value => value.StartedAt <= boundaries[index] && value.EndedAt >= boundaries[index + 1])
                });
            }
        }
        return result;
    }
}
