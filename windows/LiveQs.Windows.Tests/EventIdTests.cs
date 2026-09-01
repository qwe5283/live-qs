using LiveQs.Windows.Infrastructure.Sync;

namespace LiveQs.Windows.Tests;

public sealed class EventIdTests
{
    // Independent source of truth: Python `uuid.uuid5(uuid.UUID('10eace7c-1a13-4a4c-af9c-5f4a1d2b3c9d'), name)`.
    [Theory]
    [InlineData("device-1", "install-1", 42, "3dff000c-a867-5737-9ba3-00ee97140c98")]
    [InlineData("device-1", "install-1", 43, "c059eec1-35e5-54c3-94e8-ca80559b4e68")]
    public void ForSegment_MatchesRfc4122Uuid5(string deviceId, string installId, long segmentId, string expected)
    {
        Assert.Equal(Guid.Parse(expected), EventIds.ForSegment(deviceId, installId, segmentId));
    }

    [Fact]
    public void ForSegment_IsStableAndDistinctPerSegment()
    {
        var first = EventIds.ForSegment("device-1", "install-1", 7);
        Assert.Equal(first, EventIds.ForSegment("device-1", "install-1", 7));
        Assert.NotEqual(first, EventIds.ForSegment("device-1", "install-1", 8));
        Assert.NotEqual(first, EventIds.ForSegment("device-2", "install-1", 7));
        Assert.NotEqual(first, EventIds.ForSegment("device-1", "install-2", 7));
    }
}
