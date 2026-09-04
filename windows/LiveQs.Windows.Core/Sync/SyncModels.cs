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
/// Owner correction. <see cref="ErrorCode"/> carries the stable error code of
/// a permanent rejection (for example invalid_event or insufficient_scope) so
/// diagnostics never depend on parsing free-form text.
/// </summary>
public sealed record SyncOutcome(
    SyncQueueItem Item,
    SyncOutcomeKind Kind,
    string? Error,
    Contracts.EventAcknowledgementStatus Status = Contracts.EventAcknowledgementStatus.Accepted,
    string? ErrorCode = null);

public sealed record SyncStatus(
    bool Enabled,
    bool IsRunning,
    int PendingCount,
    DateTimeOffset? LastSuccessAt,
    string LastError)
{
    public static SyncStatus Disabled { get; } = new(false, false, 0, null, "");
}

/// <summary>One recent sync error as it is shown in diagnostics: a stable code and a safe summary.</summary>
public sealed record SyncErrorEntry(string Code, string Message, DateTimeOffset OccurredAt);

/// <summary>
/// The device's synchronization-state snapshot pushed to the service on the
/// sync cadence: counts, timestamps, and stable-code errors only — no raw
/// window titles, executable paths, or tokens ever travel in a snapshot.
/// </summary>
public sealed record SyncDiagnosticsSnapshot(
    DateTimeOffset? CollectedAt,
    DateTimeOffset? LastSuccessfulUploadAt,
    DateTimeOffset? OldestPendingAt,
    int PendingCount,
    int PermanentFailureCount,
    IReadOnlyList<SyncErrorEntry> RecentErrors);

/// <summary>Queue-state facts the sync worker reads from local storage to build a diagnostics snapshot.</summary>
public sealed record SyncQueueOverview(
    int PendingCount,
    int PermanentFailureCount,
    DateTimeOffset? OldestPendingAt,
    DateTimeOffset? LastCollectionAt,
    DateTimeOffset? LastSuccessfulUploadAt);
