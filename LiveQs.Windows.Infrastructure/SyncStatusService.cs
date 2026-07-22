using LiveQs.Windows.Core;

namespace LiveQs.Windows.Infrastructure;

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
