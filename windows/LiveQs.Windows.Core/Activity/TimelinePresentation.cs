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
    private static readonly TimeSpan _shortAfk = TimeSpan.FromMinutes(5);
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
        foreach (var run in FoldShortAfkRuns(runs))
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

            var duration = TimeSpan.FromTicks(run.Where(slice => !slice.Record.IsAfk).Sum(slice => slice.Duration.Ticks));
            if (run.All(slice => slice.Record.IsAfk) || duration >= _sustainedApplication)
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

    private static IReadOnlyList<List<ActivitySlice>> FoldShortAfkRuns(List<List<ActivitySlice>> runs)
    {
        var folded = new List<List<ActivitySlice>>();
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            if (!run[0].Record.IsAfk || run[^1].EndedAt - run[0].StartedAt > _shortAfk)
            {
                folded.Add(run);
                continue;
            }

            var previous = folded.LastOrDefault();
            var next = index + 1 < runs.Count ? runs[index + 1] : null;
            var joinsNext = next is not null && !next[0].Record.IsAfk &&
                next[0].StartedAt - run[^1].EndedAt <= _gapBoundary;
            if (previous is not null && previous.Any(slice => !slice.Record.IsAfk) &&
                run[0].StartedAt - previous[^1].EndedAt <= _gapBoundary)
            {
                previous.AddRange(run);
                // Preserve a single-application run across a short break, even when both sides are sustained.
                if (joinsNext && SameApplication(previous.Last(slice => !slice.Record.IsAfk), next![0]))
                {
                    previous.AddRange(next!);
                    index++;
                }
            }
            else if (joinsNext)
            {
                folded.Add([.. run, .. next!]);
                index++;
            }
            else
            {
                folded.Add(run);
            }
        }
        return folded;
    }

    private static TimelineEpisode CreateEpisode(ActivitySlice[] slices)
    {
        var activeSlices = slices.Where(slice => !slice.Record.IsAfk).ToArray();
        var applications = activeSlices
            .GroupBy(slice => slice.Record.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TimelineApplication(group.Key, group.Last().Record.AppName, group.Last().Record.Color,
                TimeSpan.FromTicks(group.Sum(slice => slice.Duration.Ticks))))
            .OrderByDescending(application => application.Duration).ThenBy(application => application.AppId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var switches = 0;
        var titleChanges = 0;
        for (var index = 1; index < activeSlices.Length; index++)
        {
            if (!SameApplication(activeSlices[index - 1], activeSlices[index]))
            {
                switches++;
            }
            else if (!string.Equals(activeSlices[index - 1].Record.WindowTitle, activeSlices[index].Record.WindowTitle, StringComparison.Ordinal))
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
