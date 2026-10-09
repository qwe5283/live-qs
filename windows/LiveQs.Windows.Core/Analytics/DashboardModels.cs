namespace LiveQs.Windows.Core.Analytics;

public sealed record AppUsage(
    string AppId,
    string AppName,
    string Category,
    TimeSpan Duration,
    double Share,
    string Color)
{
    public string ExecutablePath { get; init; } = "";
}

public sealed record DashboardSnapshot(
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    TimeSpan ActiveDuration,
    TimeSpan AfkDuration,
    int AppCount,
    IReadOnlyList<AppUsage> Apps,
    IReadOnlyList<ActiveTimeBucket> ActiveBuckets);

public sealed record ActiveTimeBucket(
    DateTime LocalStart,
    TimeSpan PeriodDuration,
    TimeSpan ActiveDuration)
{
    public double Share => PeriodDuration > TimeSpan.Zero
        ? Math.Clamp(ActiveDuration.TotalSeconds / PeriodDuration.TotalSeconds, 0, 1)
        : 0;
}
