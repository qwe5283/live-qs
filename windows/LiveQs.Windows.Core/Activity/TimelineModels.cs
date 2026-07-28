namespace LiveQs.Windows.Core.Activity;

public readonly record struct TimelineCursor(DateTimeOffset StartedAt, long Id);

public sealed record TimelinePage(
    IReadOnlyList<ActivitySegment> Items,
    TimelineCursor? NextCursor,
    bool HasMore);
