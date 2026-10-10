namespace LiveQs.Windows.Core.Activity;

public readonly record struct TimelineCursor(DateTimeOffset StartedAt, long Id);

public sealed record TimelinePage(
    IReadOnlyList<ActivitySegment> Items,
    TimelineCursor? NextCursor,
    bool HasMore);

public sealed record TimelineApplication(string AppId, string AppName, string Color, TimeSpan Duration);

public sealed record TimelineEpisode(
    long Key,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<ActivitySlice> Slices,
    IReadOnlyList<TimelineApplication> Applications,
    bool IsAfk,
    bool IsPartial,
    int ApplicationSwitches,
    int TitleChanges)
{
    public bool IsGap => Slices.Count == 0;
    public TimeSpan Span => EndedAt - StartedAt;
    public TimeSpan ObservedDuration => TimeSpan.FromTicks(Slices.Sum(slice => slice.Duration.Ticks));
    public TimeSpan ActiveDuration => TimeSpan.FromTicks(Slices.Where(slice => !slice.Record.IsAfk).Sum(slice => slice.Duration.Ticks));
}
