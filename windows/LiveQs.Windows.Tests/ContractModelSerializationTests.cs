using System.Text.Json;
using LiveQs.Windows.Core.Contracts;

namespace LiveQs.Windows.Tests;

public sealed class ContractModelSerializationTests
{
    [Fact]
    public void ActivityInterval_UsesContractWireNamesAndValues()
    {
        var model = new VersionedEvent
        {
            EventId = Guid.Parse("018f62d6-4f34-7c82-9085-57c8af1d7a44"),
            EventType = EventType.ActivityInterval,
            SchemaVersion = 1,
            OwnerId = "owner",
            Source = new Source { Kind = SourceKind.WindowsForeground, RecordId = "segment-1042" },
            Device = new Device { Id = "windows-workstation", Platform = Platform.Windows },
            StartAt = "2026-07-28T01:00:00.000Z",
            EndAt = "2026-07-28T01:05:00.000Z",
            CaptureTimezone = "Asia/Shanghai",
            CaptureOffsetMinutes = 480,
            PrivacyLevel = PrivacyLevel.Normal,
            Revision = 1,
            FinalizationState = FinalizationState.Final,
            Provenance = new Provenance
            {
                CollectorVersion = "0.1.0",
                ObservedAt = "2026-07-28T01:05:01.000Z",
            },
            Invalidated = false,
            Payload = new Payload
            {
                ApplicationId = "msedge.exe",
                IsAfk = false,
                Duration = new Duration { Value = 300_000, Unit = DurationUnit.Ms },
            },
        };

        var json = JsonSerializer.Serialize(model, ContractJson.Options);

        Assert.Contains("\"event_type\":\"activity.interval\"", json);
        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"kind\":\"windows.foreground\"", json);
        Assert.Contains("\"unit\":\"ms\"", json);
    }
}
