using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Update;

namespace LiveQs.Windows.Infrastructure.Update;

public sealed class UpdateStatusService : IUpdateStatusService
{
    private readonly object _gate = new();
    private UpdateStatus _current = new(UpdateCheckState.Idle, Enabled: true);

    public UpdateStatus Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<UpdateStatus>? Changed;

    public void Update(UpdateStatus status)
    {
        lock (_gate) _current = status;
        Changed?.Invoke(this, status);
    }
}
