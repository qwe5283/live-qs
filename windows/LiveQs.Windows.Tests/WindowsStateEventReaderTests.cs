using System.Text.Json;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Infrastructure.Windows;

namespace LiveQs.Windows.Tests;

public sealed class WindowsStateEventReaderTests
{
    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 506, true, DeviceStateKind.Sleep)]
    [InlineData("Microsoft-Windows-Kernel-Power", 507, false, DeviceStateKind.Sleep)]
    [InlineData("Microsoft-Windows-Kernel-Power", 42, true, DeviceStateKind.Sleep)]
    [InlineData("Microsoft-Windows-Kernel-Power", 107, false, DeviceStateKind.Sleep)]
    [InlineData("Microsoft-Windows-Kernel-General", 13, true, DeviceStateKind.Shutdown)]
    [InlineData("Microsoft-Windows-Kernel-General", 12, false, DeviceStateKind.Shutdown)]
    public void PlatformEventXml_ProducesOnlyStableStructuredEvidence(string provider, int id, bool entering, DeviceStateKind kind)
    {
        var xml = Xml(provider, id, "<Data Name='UserName'>private-user</Data><Data Name='Message'>private-text</Data>");
        var value = Assert.Single(WindowsStateEventReader.Parse(xml));

        Assert.Equal(entering, value.IsEntering);
        Assert.Equal(kind, value.Kind);
        Assert.Equal(49, value.RecordId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T16:15:11Z"), value.OccurredAt);
        Assert.Equal(value.Key, Assert.Single(WindowsStateEventReader.Parse(xml)).Key);
        var serialized = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("private-user", serialized);
        Assert.DoesNotContain("private-text", serialized);
    }

    [Fact]
    public void ResumeReport_UsesReportedSleepAndWakeTimesRatherThanLogDeliveryTime()
    {
        var events = WindowsStateEventReader.Parse(Xml("Microsoft-Windows-Power-Troubleshooter", 1,
            "<Data Name='SleepTime'>2026-10-07T16:00:00Z</Data><Data Name='WakeTime'>2026-10-07T16:10:00Z</Data>"));

        var interval = Assert.Single(DeviceStateTimeline.Pair(events));
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T16:00:00Z"), interval.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T16:10:00Z"), interval.EndedAt);
    }

    [Fact]
    public void UninitializedSleepTimestamp_DoesNotCreateAnUnboundedAwayInterval()
    {
        Assert.Empty(WindowsStateEventReader.Parse(Xml("Microsoft-Windows-Power-Troubleshooter", 1,
            "<Data Name='SleepTime'>1601-01-01T00:00:00Z</Data><Data Name='WakeTime'>2026-10-07T16:10:00Z</Data>")));
        Assert.Empty(WindowsStateEventReader.Parse(Xml("Microsoft-Windows-Kernel-Power", 566, "")));
    }

    private static string Xml(string provider, int id, string data) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System><Provider Name="{provider}"/><EventID>{id}</EventID><EventRecordID>49</EventRecordID>
            <TimeCreated SystemTime="2026-10-07T16:15:11.0000000Z"/></System>
          <EventData>{data}</EventData>
        </Event>
        """;
}
