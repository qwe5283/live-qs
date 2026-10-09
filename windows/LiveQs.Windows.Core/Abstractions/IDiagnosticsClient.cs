using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Core.Abstractions;

/// <summary>
/// Pushes the device's synchronization-state snapshot to the service on the
/// sync cadence. Snapshots carry counts, timestamps, and stable-code errors
/// only; a push failure never breaks the sync loop — the next cadence retries.
/// </summary>
public interface IDiagnosticsClient
{
    Task PushAsync(
        SyncDiagnosticsSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}
