namespace LiveQs.Windows.Core.Abstractions;

public interface IUpdateStatusService
{
    Update.UpdateStatus Current
    {
        get;
    }
    event EventHandler<Update.UpdateStatus>? Changed;
    void Update(Update.UpdateStatus status);
}
