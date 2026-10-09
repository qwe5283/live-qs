namespace LiveQs.Windows.ViewModels;

public sealed record TimelineMark(double Start, double End, string Color, string Description);

public sealed record TimelineApplicationRow(
    string AppId,
    string Name,
    string Color,
    string Duration,
    IReadOnlyList<TimelineMark> Marks);

public sealed record TimelineWindow(DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Label);

public sealed record TimelineRawRow(
    long Id,
    string TimeRange,
    string AppName,
    string Title,
    string Duration,
    string Flags);
