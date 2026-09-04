using LiveQs.Windows.Controls;
using LiveQs.Windows.Services;

namespace LiveQs.Windows.Tests;

public sealed class TrayIconStateTests
{
    [Theory]
    [InlineData(true, false, null, false, TrayIconState.Paused)]
    [InlineData(true, true, "server unavailable", false, TrayIconState.Paused)]
    [InlineData(false, false, null, false, TrayIconState.Local)]
    [InlineData(false, true, null, false, TrayIconState.CloudConnected)]
    [InlineData(false, true, "", false, TrayIconState.CloudConnected)]
    [InlineData(false, true, "server unavailable", false, TrayIconState.CloudUnavailable)]
    [InlineData(false, true, null, true, TrayIconState.UpdateAvailable)]
    [InlineData(false, false, null, true, TrayIconState.UpdateAvailable)]
    [InlineData(false, true, "server unavailable", true, TrayIconState.CloudUnavailable)]
    [InlineData(true, true, null, true, TrayIconState.Paused)]
    public void ResolveIconState_UsesExpectedPriority(
        bool samplingPaused,
        bool cloudSyncEnabled,
        string? syncError,
        bool updateAvailable,
        TrayIconState expected)
    {
        var actual = TrayIconService.ResolveIconState(samplingPaused, cloudSyncEnabled, syncError, updateAvailable);

        Assert.Equal(expected, actual);
    }
}
