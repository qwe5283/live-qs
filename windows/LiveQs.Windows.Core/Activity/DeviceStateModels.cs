namespace LiveQs.Windows.Core.Activity;

public enum DeviceStateKind
{
    Sleep,
    Locked,
    Shutdown
}

public sealed record DeviceStateEvent(
    string Key,
    DateTimeOffset OccurredAt,
    DeviceStateKind Kind,
    bool IsEntering,
    string Source,
    string Description,
    int? EventId = null,
    long? RecordId = null)
{
    public int SchemaVersion { get; init; } = 1;
}

public sealed record DeviceStateInterval(DeviceStateEvent Start, DeviceStateEvent End)
{
    public DateTimeOffset StartedAt => Start.OccurredAt;
    public DateTimeOffset EndedAt => End.OccurredAt;
    public string Description => Start.Description;
}

/// <summary>A bounded display rule after a logged resume or boot; the original event time remains a fact.</summary>
public sealed record DeviceRecoveryWindow(DeviceStateEvent Event, DateTimeOffset EndedAt)
{
    public DateTimeOffset StartedAt => Event.OccurredAt;
    public DateTimeOffset Deadline => StartedAt.AddMinutes(2);
    public DeviceStateInterval? PriorState
    {
        get; init;
    }
    public DateTimeOffset KnownStartedAt => PriorState?.StartedAt ?? StartedAt;
}

public static class DeviceStateTimeline
{
    public static IReadOnlyList<DeviceRecoveryWindow> CreateRecoveryWindows(
        IEnumerable<DeviceStateEvent> events, IReadOnlyList<DeviceStateInterval> intervals,
        Func<DeviceStateEvent, DateTimeOffset?> firstSampleAt, DateTimeOffset observedUntil)
    {
        var windows = new List<DeviceRecoveryWindow>();
        foreach (var value in events.DistinctBy(value => value.Key).Where(value => !value.IsEntering &&
            value.Kind is DeviceStateKind.Sleep or DeviceStateKind.Shutdown &&
            value.Source.StartsWith("system:", StringComparison.Ordinal) && value.EventId != 507 &&
            value.Source != "system:modern-standby"))
        {
            var end = value.OccurredAt.AddMinutes(2);
            if (firstSampleAt(value) is { } sampleAt && sampleAt < end)
            {
                end = sampleAt;
            }
            if (observedUntil < end)
            {
                end = observedUntil;
            }
            if (end > value.OccurredAt)
            {
                windows.Add(new DeviceRecoveryWindow(value, end)
                {
                    PriorState = intervals.FirstOrDefault(interval => interval.End.Key == value.Key)
                });
            }
        }
        return windows;
    }

    public static IReadOnlyList<DeviceStateInterval> Pair(IEnumerable<DeviceStateEvent> events)
    {
        var intervals = new List<DeviceStateInterval>();
        foreach (var stream in events.DistinctBy(value => value.Key).GroupBy(value => (value.Source, value.Kind)))
        {
            DeviceStateEvent? start = null;
            foreach (var value in stream.OrderBy(value => value.OccurredAt).ThenBy(value => value.IsEntering))
            {
                if (value.IsEntering)
                {
                    // A fresh transition supersedes an unmatched start; a missing exit cannot cover intervening history.
                    start = value;
                }
                else if (start is not null)
                {
                    if (value.OccurredAt > start.OccurredAt)
                    {
                        intervals.Add(new DeviceStateInterval(start, value));
                    }
                    start = null;
                }
            }
        }
        return intervals.OrderBy(interval => interval.StartedAt).ToArray();
    }
}

public static class WindowsActivityState
{
    public static bool IsLockScreen(string appId) =>
        string.Equals(appId, "lockapp.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(appId, "logonui.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(appId, "windows.locked", StringComparison.OrdinalIgnoreCase);

    public static bool IsAfk(string appId, double idleSeconds, int thresholdSeconds, bool sessionLocked) =>
        sessionLocked || IsLockScreen(appId) || idleSeconds >= thresholdSeconds;
}
