using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Core.Abstractions;

/// <summary>
/// Device-side surface of explicit historical reclassification: polling for
/// an open task and reporting outcome counts after a pass. The raw context
/// never leaves the device; only counts and already-derived revisions travel.
/// </summary>
public interface IReclassificationClient
{
    /// <summary>The open task this device should process, or null when there is none or it was already reported.</summary>
    Task<ReclassificationAssignment?> GetAssignmentAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Reports the outcome counts of a completed pass; a transport-level failure throws and the next pass re-reports.</summary>
    Task ReportAsync(
        Guid taskId,
        ReclassificationReport report,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}
