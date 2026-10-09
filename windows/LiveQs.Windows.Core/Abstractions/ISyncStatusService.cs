using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Core.Abstractions;

public interface ISyncStatusService
{
    SyncStatus Current
    {
        get;
    }
    event EventHandler<SyncStatus>? Changed;
    void Update(SyncStatus status);
}
