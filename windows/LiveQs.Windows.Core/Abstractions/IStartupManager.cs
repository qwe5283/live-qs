namespace LiveQs.Windows.Core.Abstractions;

public interface IStartupManager
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
