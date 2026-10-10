using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Analytics;

public static class DashboardStatistics
{
    public static DashboardSnapshot Calculate(
        IReadOnlyList<ActivitySegment> records,
        DateRange range,
        TimeZoneInfo timeZone,
        IReadOnlyList<DeviceStateEvent>? stateEvents = null)
    {
        var slices = ActivityIntervals.ApplyDeviceStates(ActivityIntervals.CreateSlices(records, range), stateEvents ?? []);
        var active = TimeSpan.Zero;
        var afk = TimeSpan.Zero;
        var applications = new Dictionary<string, ApplicationTotals>(StringComparer.OrdinalIgnoreCase);
        foreach (var slice in slices.Reverse())
        {
            if (slice.Duration <= TimeSpan.Zero)
            {
                continue;
            }
            if (slice.IsAfk)
            {
                afk += slice.Duration;
                continue;
            }

            active += slice.Duration;
            var record = slice.Record;
            if (!applications.TryGetValue(record.AppId, out var current))
            {
                current = new ApplicationTotals(
                    record.AppName, record.Category, record.Color, record.ExecutablePath, TimeSpan.Zero);
            }
            // Newest metadata wins, with an older path used only when the latest one is unavailable.
            applications[record.AppId] = current with
            {
                Duration = current.Duration + slice.Duration,
                ExecutablePath = string.IsNullOrWhiteSpace(current.ExecutablePath)
                    ? record.ExecutablePath
                    : current.ExecutablePath,
            };
        }

        var usage = applications.Select(application => new AppUsage(
            application.Key, application.Value.Name, application.Value.Category, application.Value.Duration,
            active > TimeSpan.Zero ? application.Value.Duration.TotalSeconds / active.TotalSeconds : 0,
            application.Value.Color)
        {
            ExecutablePath = application.Value.ExecutablePath,
        }).OrderByDescending(application => application.Duration).ToArray();
        var buckets = ActiveTimeDistribution.BuildFromSlices(range, slices, timeZone);
        return new DashboardSnapshot(range.Start, range.End, active, afk, usage.Length, usage, buckets);
    }

    private readonly record struct ApplicationTotals(
        string Name, string Category, string Color, string ExecutablePath, TimeSpan Duration);
}
