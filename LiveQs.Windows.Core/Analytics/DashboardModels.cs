using LiveQs.Windows.Core.Activity;

namespace LiveQs.Windows.Core.Analytics;

public sealed record AppUsage(
    string AppId,
    string AppName,
    string Category,
    TimeSpan Duration,
    double Share,
    string Color);

public sealed record DashboardSnapshot(
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    TimeSpan ActiveDuration,
    TimeSpan AfkDuration,
    int AppCount,
    IReadOnlyList<AppUsage> Apps);
