using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Core.Abstractions;

public interface IHeartbeatClient
{
    /// <summary>Uploads one heartbeat; a transport-level failure throws and the next cadence retries.</summary>
    Task SendAsync(HeartbeatState state, AppSettings settings, CancellationToken cancellationToken = default);
}
