using LiveQs.Windows.Core.Classification;

namespace LiveQs.Windows.Core.Sync;

/// <summary>
/// One pending outbox entry: the current state of a local activity segment.
/// <see cref="SyncVersion"/> is the event revision to upload; it increases on
/// every extension and once more when the segment is finalized.
/// <see cref="UploadOutcome"/> is the classification the server accepted for
/// the segment's latest uploaded revision (null when unknown, e.g. for
/// segments uploaded by earlier collector versions); explicit historical
/// reclassification compares a re-computed outcome against it.
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
    bool Finalized,
    ClassificationOutcome? UploadOutcome = null);

public enum SyncOutcomeKind
{
    /// <summary>The server accepted or already holds the uploaded revision; the outbox entry may be removed.</summary>
    Acknowledged,

    /// <summary>The server permanently rejected the event; the outbox entry must never retry.</summary>
    Rejected,
}

/// <summary>
/// The batch acknowledgement for one uploaded item. <see cref="Status"/> keeps
/// the contract-level answer (accepted, duplicate, stale_revision, rejected)
/// even within the coarse <see cref="SyncOutcomeKind.Acknowledged"/> bucket,
/// so reclassification can tell a settled revision from a yield to a manual
/// Owner correction.
/// </summary>
public sealed record SyncOutcome(
    SyncQueueItem Item,
    SyncOutcomeKind Kind,
    string? Error,
    Contracts.EventAcknowledgementStatus Status = Contracts.EventAcknowledgementStatus.Accepted);

public sealed record SyncStatus(
    bool Enabled,
    bool IsRunning,
    int PendingCount,
    DateTimeOffset? LastSuccessAt,
    string LastError)
{
    public static SyncStatus Disabled { get; } = new(false, false, 0, null, "");
}
