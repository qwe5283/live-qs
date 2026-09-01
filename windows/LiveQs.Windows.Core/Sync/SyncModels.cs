namespace LiveQs.Windows.Core.Sync;

/// <summary>
/// One pending outbox entry: the current state of a local activity segment.
/// <see cref="SyncVersion"/> is the event revision to upload; it increases on
/// every extension and once more when the segment is finalized.
/// </summary>
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
    bool IsFullscreen,
    int SyncVersion,
    bool Finalized);

public enum SyncOutcomeKind
{
    /// <summary>The server accepted or already holds the uploaded revision; the outbox entry may be removed.</summary>
    Acknowledged,

    /// <summary>The server permanently rejected the event; the outbox entry must never retry.</summary>
    Rejected,
}

public sealed record SyncOutcome(SyncQueueItem Item, SyncOutcomeKind Kind, string? Error);

public sealed record SyncStatus(
    bool Enabled,
    bool IsRunning,
    int PendingCount,
    DateTimeOffset? LastSuccessAt,
    string LastError)
{
    public static SyncStatus Disabled { get; } = new(false, false, 0, null, "");
}
