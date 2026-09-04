using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Core.Reclassification;

/// <summary>An open reclassification task the device should work on.</summary>
public sealed record ReclassificationAssignment(
    Guid TaskId,
    long TargetRuleSetVersion,
    DateTimeOffset? From,
    DateTimeOffset? To);

/// <summary>The outcome counts one device reports after processing an assigned task.</summary>
public sealed record ReclassificationReport(int Scanned, int Reclassified, int Unchanged, int Failed);

public enum ReclassificationAction
{
    /// <summary>The re-computed interpretation differs from what the server holds; submit a higher revision.</summary>
    SubmitRevision,

    /// <summary>The re-computed interpretation already matches; submitting would burn a no-op revision.</summary>
    LeaveUnchanged,
}

/// <summary>
/// One segment's reclassification verdict. <see cref="Segment"/> carries the
/// bumped revision when the action is SubmitRevision, and <see cref="Outcome"/>
/// is the interpretation this pass computed (null means no rule matched, which
/// legitimately strips a subject a removed rule once produced).
/// </summary>
public sealed record ReclassificationDecision(
    SyncQueueItem Segment,
    ClassificationOutcome? Outcome,
    ReclassificationAction Action);

/// <summary>
/// Decides, per locally retained finalized segment, whether an explicit
/// reclassification pass must submit a higher revision. The comparison runs
/// against the recorded upload outcome — the classification the server
/// accepted for the segment's latest revision — so unchanged events never
/// burn a revision. Segments without a recorded outcome (uploaded by earlier
/// collector versions) only ever gain a subject: a rule-derived subject is
/// added when the engine now matches, but nothing is stripped that cannot be
/// verified locally.
/// </summary>
public static class ReclassificationPlanner
{
    public static ReclassificationDecision Decide(SyncQueueItem segment, ClassificationOutcome? recomputed)
    {
        var bumped = segment with { SyncVersion = segment.SyncVersion + 1 };
        var recorded = segment.UploadOutcome;
        var changed = recorded is null ? recomputed is not null : !SameOutcome(recorded, recomputed);
        return new ReclassificationDecision(
            bumped,
            recomputed,
            changed ? ReclassificationAction.SubmitRevision : ReclassificationAction.LeaveUnchanged);
    }

    private static bool SameOutcome(ClassificationOutcome? recorded, ClassificationOutcome? recomputed) =>
        recorded is null && recomputed is null
        || (recorded is not null && recomputed is not null
            && string.Equals(recorded.SubjectId, recomputed.SubjectId, StringComparison.Ordinal)
            && string.Equals(recorded.RuleId, recomputed.RuleId, StringComparison.Ordinal)
            && recorded.RuleVersion == recomputed.RuleVersion
            && recorded.Confidence.Equals(recomputed.Confidence));
}
