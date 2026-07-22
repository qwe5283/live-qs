namespace LiveQs.Windows.Core;

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
    public bool StartMinimized { get; init; }
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

public sealed record ActivitySample(
    DateTimeOffset CapturedAt,
    string AppId,
    string AppName,
    string ExecutablePath,
    string WindowTitle,
    string WindowTitleHash,
    double IdleSeconds,
    bool IsAfk,
    bool IsAudioPlaying,
    bool IsFullscreen,
    int? BatteryPercent,
    bool? BatteryCharging);

public sealed record ActivitySegment(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string AppId,
    string AppName,
    string WindowTitle,
    bool IsAfk,
    bool IsAudioPlaying,
    bool IsFullscreen,
    string Category,
    string Color)
{
    public TimeSpan Duration => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;
}

public readonly record struct TimelineCursor(DateTimeOffset StartedAt, long Id);

public sealed record TimelinePage(
    IReadOnlyList<ActivitySegment> Items,
    TimelineCursor? NextCursor,
    bool HasMore);

public sealed record AppUsage(
    string AppId,
    string AppName,
    string Category,
    TimeSpan Duration,
    double Share,
    string Color);

public sealed record DashboardSnapshot(
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    TimeSpan ActiveDuration,
    TimeSpan AfkDuration,
    int AppCount,
    IReadOnlyList<AppUsage> Apps);

public sealed record ApplicationRule(
    string AppId,
    string Alias,
    string Category,
    bool IsExcluded);

public sealed record SyncQueueItem(
    long SegmentId,
    int AttemptCount,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string AppId,
    string AppName,
    string WindowTitle,
    string WindowTitleHash,
    bool IsAfk,
    bool IsAudioPlaying,
    bool IsFullscreen);

public sealed record SyncStatus(
    bool Enabled,
    bool IsRunning,
    int PendingCount,
    DateTimeOffset? LastSuccessAt,
    string LastError)
{
    public static SyncStatus Disabled { get; } = new(false, false, 0, null, "");
}

public readonly record struct DateRange(DateTimeOffset Start, DateTimeOffset End)
{
    public static DateRange FromLocalDates(DateTime startDate, DateTime endDateInclusive)
    {
        var start = new DateTimeOffset(startDate.Date, TimeZoneInfo.Local.GetUtcOffset(startDate.Date));
        var endDate = endDateInclusive.Date.AddDays(1);
        var end = new DateTimeOffset(endDate, TimeZoneInfo.Local.GetUtcOffset(endDate));
        return new DateRange(start, end);
    }

    public static DateRange Today() => FromLocalDates(DateTime.Today, DateTime.Today);
}

public static class DurationText
{
    public static string Format(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}小时 {duration.Minutes}分钟";
        if (duration.TotalMinutes >= 1) return $"{Math.Max(1, (int)duration.TotalMinutes)}分钟";
        return $"{Math.Max(0, (int)duration.TotalSeconds)}秒";
    }
}
