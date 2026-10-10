namespace LiveQs.Windows.Core.Activity;

public readonly record struct TimelineCursor(DateTimeOffset StartedAt, long Id);

public sealed record TimelinePage(
    IReadOnlyList<ActivitySegment> Items,
    TimelineCursor? NextCursor,
    bool HasMore)
{
    public IReadOnlyList<DeviceStateEvent> StateEvents { get; init; } = [];
    public IReadOnlyList<DeviceRecoveryWindow>? RecoveryWindows
    {
        get; init;
    }
    public string StateWarning { get; init; } = "";
}

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
    public IReadOnlyList<ActivitySegment> Records
    {
        get; init;
    } =
        Slices.Select(slice => slice.Record).DistinctBy(record => record.Id).ToArray();
    public IReadOnlyList<DeviceStateInterval> StateEvidence { get; init; } = [];
    public IReadOnlyList<DeviceRecoveryWindow> RecoveryWindows { get; init; } = [];
    public TimeSpan SystemCoveredDuration
    {
        get; init;
    }
    public TimeSpan RecoveryCoveredDuration
    {
        get; init;
    }
    public bool ContinuesBeforeRange
    {
        get; init;
    }
    public bool ContinuesAfterRange
    {
        get; init;
    }
    public int RecordCount => Records.Count;
    public bool IsGap => Slices.Count == 0 && StateEvidence.Count == 0 && RecoveryWindows.Count == 0;
    public TimeSpan Span => EndedAt - StartedAt;
    public TimeSpan ObservedDuration => TimeSpan.FromTicks(Slices.Sum(slice => slice.Duration.Ticks));
    public TimeSpan ActiveDuration => TimeSpan.FromTicks(Slices.Where(slice => !slice.IsAfk).Sum(slice => slice.Duration.Ticks));
}
