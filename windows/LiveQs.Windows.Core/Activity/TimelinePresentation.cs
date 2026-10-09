using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Activity;

/// <summary>
/// A local display projection. Records remain intact; later foreground records
/// take precedence over overlap for marks and duration, never for persistence.
/// </summary>
public static class TimelinePresentation
{
    private static readonly TimeSpan _gapBoundary = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _sustainedApplication = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _maximumMixedSpan = TimeSpan.FromHours(1);

    public static IReadOnlyList<TimelineEpisode> Build(
        IEnumerable<ActivitySegment> records, DateRange range, bool hasEarlierRecords = false)
    {
        var slices = ActivityIntervals.CreateSlices(records, range);

        var runs = new List<List<ActivitySlice>>();
        foreach (var slice in slices)
        {
            var previous = runs.LastOrDefault();
            if (previous is not null && previous[^1].Record.IsAfk == slice.Record.IsAfk &&
                (slice.Record.IsAfk || SameApplication(previous[^1], slice)) &&
                slice.StartedAt - previous[^1].EndedAt <= _gapBoundary)
            {
                previous.Add(slice);
            }
            else
            {
                runs.Add([slice]);
            }
        }

        var episodes = new List<TimelineEpisode>();
        var pending = new List<ActivitySlice>();
        foreach (var run in runs)
        {
            var first = run[0];
            if (pending.Count > 0 && first.StartedAt - pending[^1].EndedAt > _gapBoundary)
            {
                Flush();
            }

            if (pending.Count == 0 && episodes.Count > 0 && first.StartedAt - episodes[^1].EndedAt > _gapBoundary)
            {
                episodes.Add(new TimelineEpisode(-first.Record.Id, episodes[^1].EndedAt, first.StartedAt,
                    [], [], false, false, 0, 0));
            }

            var duration = TimeSpan.FromTicks(run.Sum(slice => slice.Duration.Ticks));
            if (first.Record.IsAfk || duration >= _sustainedApplication)
            {
                if (pending.Any(slice => slice.Duration > TimeSpan.Zero))
                {
                    Flush();
                }

                pending.AddRange(run);
                Flush();
                continue;
            }

            if (pending.Count > 0 && run[^1].EndedAt - pending[0].StartedAt > _maximumMixedSpan)
            {
                Flush();
            }

            pending.AddRange(run);
        }
        Flush();
        if (hasEarlierRecords)
        {
            var firstRecord = episodes.FindIndex(episode => !episode.IsGap);
            if (firstRecord >= 0)
            {
                episodes[firstRecord] = episodes[firstRecord] with
                {
                    IsPartial = true
                };
            }
        }
        episodes.Reverse();
        return episodes;

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            episodes.Add(CreateEpisode(pending.ToArray()));
            pending.Clear();
        }
    }

    private static TimelineEpisode CreateEpisode(ActivitySlice[] slices)
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
            if (!SameApplication(slices[index - 1], slices[index]))
            {
                switches++;
            }
            else if (!string.Equals(slices[index - 1].Record.WindowTitle, slices[index].Record.WindowTitle, StringComparison.Ordinal))
            {
                titleChanges++;
            }
        }
        return new TimelineEpisode(slices[0].Record.Id, slices[0].StartedAt, slices.Max(slice => slice.EndedAt),
            slices, applications, slices.All(slice => slice.Record.IsAfk), false, switches, titleChanges);
    }

    private static bool SameApplication(ActivitySlice left, ActivitySlice right) =>
        string.Equals(left.Record.AppId, right.Record.AppId, StringComparison.OrdinalIgnoreCase);
}
