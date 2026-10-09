using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Core.Abstractions;

public interface ISyncClient
{
    /// <summary>Uploads one batch and returns exactly one outcome per item; transport-level failure throws.</summary>
    Task<IReadOnlyList<SyncOutcome>> UploadAsync(
        IReadOnlyList<SyncQueueItem> items,
        AppSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Uploads pre-computed reclassification decisions (same event identity,
    /// bumped revision, re-interpreted classification) and records the local
    /// consequence of each acknowledgement on its segment; returns exactly one
    /// outcome per decision. Transport-level failure throws.
    /// </summary>
    Task<IReadOnlyList<SyncOutcome>> UploadReclassificationAsync(
        IReadOnlyList<ReclassificationDecision> decisions,
        AppSettings settings,
        CancellationToken cancellationToken);
}
