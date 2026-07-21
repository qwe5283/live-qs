using LiveQs.Windows.App;
using LiveQs.Windows.App.Controls;

namespace LiveQs.Windows.Tests;

public sealed class TrayIconStateTests
{
    [Theory]
    [InlineData(true, false, null, TrayIconState.Paused)]
    [InlineData(true, true, "server unavailable", TrayIconState.Paused)]
    [InlineData(false, false, null, TrayIconState.Local)]
    [InlineData(false, true, null, TrayIconState.CloudConnected)]
    [InlineData(false, true, "", TrayIconState.CloudConnected)]
    [InlineData(false, true, "server unavailable", TrayIconState.CloudUnavailable)]
    public void ResolveIconState_UsesExpectedPriority(
        bool samplingPaused,
        bool cloudSyncEnabled,
        string? syncError,
        TrayIconState expected)
    {
        var actual = TrayIconService.ResolveIconState(samplingPaused, cloudSyncEnabled, syncError);

        Assert.Equal(expected, actual);
    }
}
