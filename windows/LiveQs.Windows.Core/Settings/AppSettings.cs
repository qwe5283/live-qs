namespace LiveQs.Windows.Core.Settings;

public enum WindowTitleMode
{
    None,
    Hash,
    Original,
}

public sealed record AppSettings
{
    public int SamplingIntervalSeconds { get; init; } = 10;
    public int AfkThresholdSeconds { get; init; } = 90;
    public WindowTitleMode WindowTitleMode { get; init; } = WindowTitleMode.Original;
    public int RetentionDays { get; init; } = 30;
    public bool CloudSyncEnabled { get; init; }
    public string ServerBaseUrl { get; init; } = "http://localhost:8787";
    public string DeviceToken { get; init; } = "";
    public string DeviceId { get; init; } = Environment.MachineName.ToLowerInvariant();
    public bool LaunchOnStartup { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool SamplingPaused { get; init; }

    public AppSettings Normalize() => this with
    {
        SamplingIntervalSeconds = Math.Clamp(SamplingIntervalSeconds, 1, 300),
        AfkThresholdSeconds = Math.Clamp(AfkThresholdSeconds, 5, 86_400),
        RetentionDays = Math.Clamp(RetentionDays, 7, 3_650),
        ServerBaseUrl = ServerBaseUrl.Trim().TrimEnd('/'),
        DeviceToken = DeviceToken.Trim(),
        DeviceId = string.IsNullOrWhiteSpace(DeviceId) ? Environment.MachineName.ToLowerInvariant() : DeviceId.Trim(),
    };

    public string? Validate()
    {
        if (SamplingIntervalSeconds is < 1 or > 300) return "采样间隔必须在 1 到 300 秒之间。";
        if (AfkThresholdSeconds is < 5 or > 86_400) return "AFK 阈值必须在 5 到 86400 秒之间。";
        if (RetentionDays is < 7 or > 3_650) return "本地保留期必须在 7 到 3650 天之间。";
        if (!CloudSyncEnabled) return null;
        if (!Uri.TryCreate(ServerBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "云端 Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
        if (string.IsNullOrWhiteSpace(DeviceToken)) return "启用云同步时必须填写 Device Token。";
        return string.IsNullOrWhiteSpace(DeviceId) ? "启用云同步时必须填写 Device ID。" : null;
    }
}
