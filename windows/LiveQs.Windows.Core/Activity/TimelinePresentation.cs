using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Activity;

public sealed record TimelineSlice(ActivitySegment Record, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}

public sealed record TimelineApplication(string AppId, string AppName, string Color, TimeSpan Duration);

public sealed record TimelineEpisode(
    long Key,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<TimelineSlice> Slices,
    IReadOnlyList<TimelineApplication> Applications,
    bool IsAfk,
    bool IsPartial,
    int ApplicationSwitches,
    int TitleChanges)
{
    public bool IsGap => Slices.Count == 0;
    public TimeSpan Span => EndedAt - StartedAt;
    public TimeSpan ObservedDuration => TimeSpan.FromTicks(Slices.Sum(slice => slice.Duration.Ticks));
    public TimeSpan ActiveDuration => IsAfk ? TimeSpan.Zero : ObservedDuration;
}

/// <summary>
/// A local display projection. Records remain intact; later foreground records
/// take precedence over overlap for marks and duration, never for persistence.
/// </summary>
public static class TimelinePresentation
{
    private static readonly TimeSpan GapBoundary = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SustainedApplication = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumMixedSpan = TimeSpan.FromHours(1);

    public static IReadOnlyList<TimelineEpisode> Build(
        IEnumerable<ActivitySegment> records, DateRange range, bool hasEarlierRecords = false)
    {
        var ordered = records
            .Where(record => record.EndedAt > record.StartedAt && record.StartedAt < range.End && record.EndedAt > range.Start)
            .DistinctBy(record => record.Id)
            .OrderBy(record => record.StartedAt).ThenBy(record => record.Id)
            .ToArray();
        var slices = new List<TimelineSlice>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var record = ordered[index];
            var start = record.StartedAt > range.Start ? record.StartedAt : range.Start;
            var end = record.EndedAt < range.End ? record.EndedAt : range.End;
            if (index + 1 < ordered.Length && ordered[index + 1].StartedAt < end)
                end = ordered[index + 1].StartedAt;
            slices.Add(new TimelineSlice(record, start, end > start ? end : start));
        }

        var runs = new List<List<TimelineSlice>>();
        foreach (var slice in slices)
        {
            var previous = runs.LastOrDefault();
            if (previous is not null && previous[^1].Record.IsAfk == slice.Record.IsAfk &&
                (slice.Record.IsAfk || SameApplication(previous[^1], slice)) &&
                slice.StartedAt - previous[^1].EndedAt <= GapBoundary)
                previous.Add(slice);
            else
                runs.Add([slice]);
        }

        var episodes = new List<TimelineEpisode>();
        var pending = new List<TimelineSlice>();
        foreach (var run in runs)
        {
            var first = run[0];
            if (pending.Count > 0 && first.StartedAt - pending[^1].EndedAt > GapBoundary)
                Flush();
            if (pending.Count == 0 && episodes.Count > 0 && first.StartedAt - episodes[^1].EndedAt > GapBoundary)
            {
                episodes.Add(new TimelineEpisode(-first.Record.Id, episodes[^1].EndedAt, first.StartedAt,
                    [], [], false, false, 0, 0));
            }

            var duration = TimeSpan.FromTicks(run.Sum(slice => slice.Duration.Ticks));
            if (first.Record.IsAfk || duration >= SustainedApplication)
            {
                if (pending.Any(slice => slice.Duration > TimeSpan.Zero)) Flush();
                pending.AddRange(run);
                Flush();
                continue;
            }

            if (pending.Count > 0 && run[^1].EndedAt - pending[0].StartedAt > MaximumMixedSpan)
                Flush();
            pending.AddRange(run);
        }
        Flush();
        if (hasEarlierRecords)
        {
            var firstRecord = episodes.FindIndex(episode => !episode.IsGap);
            if (firstRecord >= 0) episodes[firstRecord] = episodes[firstRecord] with { IsPartial = true };
        }
        episodes.Reverse();
        return episodes;

        void Flush()
        {
            if (pending.Count == 0) return;
            episodes.Add(CreateEpisode(pending.ToArray()));
            pending.Clear();
        }
    }

    private static TimelineEpisode CreateEpisode(TimelineSlice[] slices)
    {
        var applications = slices
            .GroupBy(slice => slice.Record.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TimelineApplication(group.Key, group.Last().Record.AppName, group.Last().Record.Color,
                TimeSpan.FromTicks(group.Sum(slice => slice.Duration.Ticks))))
            .OrderByDescending(application => application.Duration).ThenBy(application => application.AppId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var switches = 0;
        var titleChanges = 0;
        for (var index = 1; index < slices.Length; index++)
        {
            if (!SameApplication(slices[index - 1], slices[index])) switches++;
            else if (!string.Equals(slices[index - 1].Record.WindowTitle, slices[index].Record.WindowTitle, StringComparison.Ordinal))
                titleChanges++;
        }
        return new TimelineEpisode(slices[0].Record.Id, slices[0].StartedAt, slices.Max(slice => slice.EndedAt),
            slices, applications, slices.All(slice => slice.Record.IsAfk), false, switches, titleChanges);
    }

    private static bool SameApplication(TimelineSlice left, TimelineSlice right) =>
        string.Equals(left.Record.AppId, right.Record.AppId, StringComparison.OrdinalIgnoreCase);
}
