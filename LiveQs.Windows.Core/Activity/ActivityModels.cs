namespace LiveQs.Windows.Core.Activity;

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
