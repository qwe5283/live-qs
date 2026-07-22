namespace LiveQs.Windows.Services;

public interface IUserDialogService
{
    void ShowError(string title, Exception exception);
    bool ConfirmDeleteRange();
    string? SelectExportPath(string defaultFileName);
}
