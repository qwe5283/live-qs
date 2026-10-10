using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Activity;

/// <summary>A local projection with separate sampling and system-state evidence.</summary>
public static class TimelinePresentation
{
    private static readonly TimeSpan _gapBoundary = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _sustainedApplication = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _shortAfk = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _maximumMixedSpan = TimeSpan.FromHours(1);

    public static IReadOnlyList<TimelineEpisode> Build(
        IEnumerable<ActivitySegment> records, DateRange range, bool hasEarlierRecords = false,
        IReadOnlyList<DeviceStateEvent>? stateEvents = null, DateTimeOffset? observedUntil = null,
        IReadOnlyList<DeviceRecoveryWindow>? recoveryWindows = null, IReadOnlyList<DateRange>? deletedStateRanges = null)
    {
        var recordArray = records.ToArray();
        var sourceSlices = ActivityIntervals.CreateSlices(recordArray,
            new DateRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        var slices = sourceSlices.Where(slice => slice.StartedAt < range.End && slice.EndedAt > range.Start)
            .Select(slice => slice with
            {
                StartedAt = slice.StartedAt > range.Start ? slice.StartedAt : range.Start,
                EndedAt = slice.EndedAt < range.End ? slice.EndedAt : range.End
            }).ToArray();
        // Earlier sampling pages are not loaded yet; do not present their absence as system-only coverage.
        var loadedRange = hasEarlierRecords && slices.Length > 0
            ? new DateRange(slices[0].StartedAt, range.End) : range;
        var intervals = DeviceStateTimeline.Pair(stateEvents ?? [], deletedStateRanges);
        var windows = (recoveryWindows ?? DeviceStateTimeline.CreateRecoveryWindows(stateEvents ?? [], intervals,
                value => FirstSampleAt(sourceSlices, value.OccurredAt), observedUntil ?? DateTimeOffset.MaxValue,
                deletedStateRanges))
            .Where(window => window.StartedAt < loadedRange.End && window.EndedAt > loadedRange.Start).ToArray();
        var pieces = CreatePieces(slices, intervals, windows, loadedRange);
        bool HasGap(DateTimeOffset end, DateTimeOffset start) => start - end > _gapBoundary ||
            (deletedStateRanges ?? []).Any(deleted => deleted.Start < start && deleted.End > end) ||
            windows.Any(window => window.EndedAt == window.Deadline && window.Deadline >= end && window.Deadline < start);
        var runs = new List<List<Piece>>();
        foreach (var piece in pieces)
        {
            var previous = runs.LastOrDefault();
            if (previous is not null && previous[^1].IsAfk == piece.IsAfk &&
                (piece.IsAfk || SameApplication(previous[^1], piece)) &&
                !HasGap(previous[^1].EndedAt, piece.StartedAt))
            {
                previous.Add(piece);
            }
            else
            {
                runs.Add([piece]);
            }
        }

        var episodes = new List<TimelineEpisode>();
        var pending = new List<Piece>();
        foreach (var run in FoldShortAfkRuns(runs, HasGap))
        {
            var first = run[0];
            if (pending.Count > 0 && HasGap(pending[^1].EndedAt, first.StartedAt))
            {
                Flush();
            }
            if (pending.Count == 0 && episodes.Count > 0 && HasGap(episodes[^1].EndedAt, first.StartedAt))
            {
                episodes.Add(new TimelineEpisode(-first.StartedAt.UtcTicks, episodes[^1].EndedAt, first.StartedAt,
                    [], [], false, false, 0, 0));
            }

            var duration = TimeSpan.FromTicks(run.Where(piece => !piece.IsAfk).Sum(piece => piece.Duration.Ticks));
            if (run.All(piece => piece.IsAfk) || duration >= _sustainedApplication)
            {
                Flush();
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
            var firstRecord = episodes.FindIndex(episode => episode.Slices.Count > 0);
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
            episodes.Add(CreateEpisode(pending, range, sourceSlices));
            pending.Clear();
        }
    }

    private static IReadOnlyList<Piece> CreatePieces(
        IReadOnlyList<ActivitySlice> slices, IReadOnlyList<DeviceStateInterval> intervals,
        IReadOnlyList<DeviceRecoveryWindow> recoveryWindows, DateRange range)
    {
        var evidence = intervals.Where(value => value.StartedAt < range.End && value.EndedAt > range.Start).ToArray();
        var boundaries = slices.SelectMany(slice => new[] { slice.StartedAt, slice.EndedAt })
            .Concat(evidence.SelectMany(value => new[]
            {
                value.StartedAt > range.Start ? value.StartedAt : range.Start,
                value.EndedAt < range.End ? value.EndedAt : range.End
            }))
            .Concat(recoveryWindows.SelectMany(window => new[]
            {
                window.StartedAt > range.Start ? window.StartedAt : range.Start,
                window.EndedAt < range.End ? window.EndedAt : range.End
            })).Distinct().Order().ToArray();
        var result = new List<Piece>();
        var sampleIndex = 0;
        for (var index = 0; index + 1 < boundaries.Length; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            while (sampleIndex < slices.Count && slices[sampleIndex].EndedAt <= start)
            {
                sampleIndex++;
            }
            var sample = sampleIndex < slices.Count && slices[sampleIndex].StartedAt <= start
                ? slices[sampleIndex] : null;
            var states = evidence.Where(value => value.StartedAt <= start && value.EndedAt >= end).ToArray();
            if (sample is not null)
            {
                sample = sample with
                {
                    StartedAt = start,
                    EndedAt = end,
                    SystemInactive = states.Length > 0
                };
            }
            var recovery = sample is null && states.Length == 0
                ? recoveryWindows.Where(window => window.StartedAt <= start && window.EndedAt >= end).ToArray() : [];
            if (sample is not null || states.Length > 0 || recovery.Length > 0)
            {
                result.Add(new Piece(start, end, sample, states, recovery));
            }
        }
        return result;
    }

    private static DateTimeOffset? FirstSampleAt(IReadOnlyList<ActivitySlice> slices, DateTimeOffset at)
    {
        var sample = slices.FirstOrDefault(slice => slice.EndedAt > at);
        return sample is null ? null : sample.StartedAt > at ? sample.StartedAt : at;
    }

    private static IReadOnlyList<List<Piece>> FoldShortAfkRuns(List<List<Piece>> runs,
        Func<DateTimeOffset, DateTimeOffset, bool> hasGap)
    {
        var folded = new List<List<Piece>>();
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            if (!run[0].IsAfk || run.Max(piece => piece.KnownEnd) - run.Min(piece => piece.KnownStart) > _shortAfk)
            {
                folded.Add(run);
                continue;
            }
            var previous = folded.LastOrDefault();
            var next = index + 1 < runs.Count ? runs[index + 1] : null;
            var joinsNext = next is not null && !next[0].IsAfk &&
                !hasGap(run[^1].EndedAt, next[0].StartedAt);
            if (previous is not null && previous.Any(piece => !piece.IsAfk) &&
                !hasGap(previous[^1].EndedAt, run[0].StartedAt))
            {
                previous.AddRange(run);
                if (joinsNext && SameApplication(previous.Last(piece => !piece.IsAfk), next![0]))
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

    private static TimelineEpisode CreateEpisode(
        IReadOnlyList<Piece> pieces, DateRange range, IReadOnlyList<ActivitySlice> sourceSlices)
    {
        var slices = pieces.Select(piece => piece.Sample).OfType<ActivitySlice>().ToArray();
        var activeSlices = slices.Where(slice => !slice.IsAfk).ToArray();
        var applications = activeSlices.GroupBy(slice => slice.Record.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TimelineApplication(group.Key, group.Last().Record.AppName, group.Last().Record.Color,
                TimeSpan.FromTicks(group.Sum(slice => slice.Duration.Ticks))))
            .OrderByDescending(application => application.Duration)
            .ThenBy(application => application.AppId, StringComparer.OrdinalIgnoreCase).ToArray();
        var switches = 0;
        var titleChanges = 0;
        for (var index = 1; index < activeSlices.Length; index++)
        {
            var previous = activeSlices[index - 1].Record;
            var current = activeSlices[index].Record;
            if (!string.Equals(previous.AppId, current.AppId, StringComparison.OrdinalIgnoreCase))
            {
                switches++;
            }
            else if (!string.Equals(previous.WindowTitle, current.WindowTitle, StringComparison.Ordinal))
            {
                titleChanges++;
            }
        }
        var evidence = pieces.SelectMany(piece => piece.Evidence).DistinctBy(value => (value.Start.Key, value.End.Key)).ToArray();
        var start = pieces[0].StartedAt;
        var end = pieces[^1].EndedAt;
        return new TimelineEpisode(slices.FirstOrDefault()?.Record.Id ?? -start.UtcTicks, start, end,
            slices, applications, pieces.All(piece => piece.IsAfk), false, switches, titleChanges)
        {
            // Fully superseded observations remain inspectable without adding coverage or changes.
            Records = slices.Select(slice => slice.Record)
                .Concat(sourceSlices.Where(slice => slice.Duration == TimeSpan.Zero &&
                    slice.StartedAt >= start && slice.StartedAt < end).Select(slice => slice.Record))
                .DistinctBy(record => record.Id).ToArray(),
            StateEvidence = evidence,
            RecoveryWindows = pieces.SelectMany(piece => piece.Recovery).DistinctBy(value => value.Event.Key).ToArray(),
            SystemCoveredDuration = TimeSpan.FromTicks(pieces.Where(piece => piece.Sample is null && piece.Evidence.Count > 0).Sum(piece => piece.Duration.Ticks)),
            RecoveryCoveredDuration = TimeSpan.FromTicks(pieces.Where(piece => piece.Recovery.Count > 0).Sum(piece => piece.Duration.Ticks)),
            ContinuesBeforeRange = start == range.Start && pieces.Min(piece => piece.KnownStart) < range.Start,
            ContinuesAfterRange = end == range.End && pieces.Max(piece => piece.KnownEnd) > range.End
        };
    }

    private static bool SameApplication(Piece left, Piece right) =>
        string.Equals(left.Sample?.Record.AppId, right.Sample?.Record.AppId, StringComparison.OrdinalIgnoreCase);

    private sealed record Piece(DateTimeOffset StartedAt, DateTimeOffset EndedAt, ActivitySlice? Sample,
        IReadOnlyList<DeviceStateInterval> Evidence, IReadOnlyList<DeviceRecoveryWindow> Recovery)
    {
        public TimeSpan Duration => EndedAt - StartedAt;
        public bool IsAfk => Sample is null || Sample.IsAfk;
        private bool HasRecordedAway => Sample is { } sample &&
            (sample.Record.IsAfk || WindowsActivityState.IsLockScreen(sample.Record.AppId));
        public DateTimeOffset KnownStart => Evidence.Select(value => value.StartedAt)
            .Concat(Recovery.Select(value => value.KnownStartedAt))
            .Append(HasRecordedAway ? Sample!.EffectiveStartedAt : StartedAt).Min();
        public DateTimeOffset KnownEnd => Evidence.Select(value => value.EndedAt)
            .Concat(Recovery.Select(value => value.EndedAt))
            .Append(HasRecordedAway ? Sample!.EffectiveEndedAt : EndedAt).Max();
    }
}
