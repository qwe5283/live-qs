namespace LiveQs.Windows.Core.Sync;

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
