namespace AiLife.WindowsAgent.Monitoring;

public sealed record ForegroundSample(
    string AppId,
    string AppName,
    string WindowTitle,
    double IdleSeconds,
    bool IsAfk,
    bool IsAudioPlaying,
    bool IsFullscreen,
    int? BatteryPercent,
    bool? BatteryCharging);

