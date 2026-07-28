using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Infrastructure.Sync;

public sealed class SyncStatusService : ISyncStatusService
{
    private readonly object _gate = new();
    private SyncStatus _current = SyncStatus.Disabled;

    public SyncStatus Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<SyncStatus>? Changed;

    public void Update(SyncStatus status)
    {
        lock (_gate) _current = status;
        Changed?.Invoke(this, status);
    }
}
