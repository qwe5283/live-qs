using LiveQs.Windows.Core;

namespace LiveQs.Windows.Tests;

public sealed class CoreTests
{
    [Fact]
    public void SettingsNormalize_ClampsAndTrimsValues()
    {
        var settings = new AppSettings
        {
            SamplingIntervalSeconds = 999,
            AfkThresholdSeconds = 1,
            RetentionDays = 99_999,
            ServerBaseUrl = " https://example.test/ ",
            DeviceId = " device-1 ",
        }.Normalize();

        Assert.Equal(300, settings.SamplingIntervalSeconds);
        Assert.Equal(5, settings.AfkThresholdSeconds);
        Assert.Equal(3650, settings.RetentionDays);
        Assert.Equal("https://example.test", settings.ServerBaseUrl);
        Assert.Equal("device-1", settings.DeviceId);
    }

    [Fact]
    public void SettingsValidate_RequiresCredentialsOnlyWhenCloudIsEnabled()
    {
        Assert.Null(new AppSettings { CloudSyncEnabled = false }.Validate());
        Assert.Contains("Device Token", new AppSettings
        {
            CloudSyncEnabled = true,
            ServerBaseUrl = "https://example.test",
            DeviceToken = "",
        }.Validate());
    }

    [Fact]
    public void DateRange_UsesExclusiveEndOfLocalDay()
    {
        var range = DateRange.FromLocalDates(new DateTime(2026, 7, 1), new DateTime(2026, 7, 3));

        Assert.Equal(new DateTime(2026, 7, 1), range.Start.LocalDateTime.Date);
        Assert.Equal(new DateTime(2026, 7, 4), range.End.LocalDateTime.Date);
        Assert.Equal(3, (range.End - range.Start).TotalDays);
    }
}
