using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using LiveQs.Windows.Core.Activity;

namespace LiveQs.Windows.Infrastructure.Windows;

public interface IWindowsStateEventReader
{
    Task<IReadOnlyList<DeviceStateEvent>> ReadAsync(DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsStateEventReader : IWindowsStateEventReader
{
    public Task<IReadOnlyList<DeviceStateEvent>> ReadAsync(DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceStateEvent>>(() =>
    {
        var from = start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var until = end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var xpath = $"*[System[TimeCreated[@SystemTime >= '{from}' and @SystemTime <= '{until}'] and " +
            "((Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=107 or EventID=506 or EventID=507)) or " +
            "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1) or " +
            "(Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13)))]]";
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
        var events = new List<DeviceStateEvent>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var value = reader.ReadEvent();
            if (value is null)
            {
                break;
            }
            events.AddRange(Parse(value.ToXml()));
        }
        return events;
    }, cancellationToken);

    public static IReadOnlyList<DeviceStateEvent> Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root is null)
        {
            return [];
        }
        var ns = root.Name.Namespace;
        var system = root.Element(ns + "System");
        var provider = system?.Element(ns + "Provider")?.Attribute("Name")?.Value;
        if (!int.TryParse(system?.Element(ns + "EventID")?.Value, out var eventId) ||
            !DateTimeOffset.TryParse(system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return [];
        }
        long.TryParse(system?.Element(ns + "EventRecordID")?.Value, out var recordId);
        var key = $"{provider}/{recordId}/{at.ToUniversalTime():O}";
        if (provider == "Microsoft-Windows-Kernel-Power")
        {
            return eventId switch
            {
                506 or 507 => [new DeviceStateEvent(key, at, DeviceStateKind.Sleep, eventId == 506,
                    "system:modern-standby", "现代待机", eventId, recordId)],
                42 or 107 => [new DeviceStateEvent(key, at, DeviceStateKind.Sleep, eventId == 42,
                    "system:sleep", "睡眠 / 休眠", eventId, recordId)],
                _ => []
            };
        }
        if (provider == "Microsoft-Windows-Kernel-General" && eventId is 12 or 13)
        {
            return [new DeviceStateEvent(key, at, DeviceStateKind.Shutdown, eventId == 13,
                "system:shutdown", "Windows 关机", eventId, recordId)];
        }
        if (provider == "Microsoft-Windows-Power-Troubleshooter" && eventId == 1)
        {
            var data = root.Element(ns + "EventData")?.Elements(ns + "Data")
                .Where(value => value.Attribute("Name") is not null)
                .ToDictionary(value => value.Attribute("Name")!.Value, value => value.Value);
            if (data is not null && data.TryGetValue("SleepTime", out var sleep) && data.TryGetValue("WakeTime", out var wake) &&
                DateTimeOffset.TryParse(sleep, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var asleep) &&
                DateTimeOffset.TryParse(wake, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var awake) &&
                asleep.Year >= 2000 && awake > asleep && awake <= at.AddMinutes(1))
            {
                return [new DeviceStateEvent(key + "/sleep", asleep, DeviceStateKind.Sleep, true,
                    "system:recovered-sleep", "睡眠 / 休眠", eventId, recordId),
                    new DeviceStateEvent(key + "/wake", awake, DeviceStateKind.Sleep, false,
                        "system:recovered-sleep", "睡眠 / 休眠", eventId, recordId)];
            }
        }
        return [];
    }
}
