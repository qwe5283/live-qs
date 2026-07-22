using Microsoft.Win32;
using System.Windows;

namespace LiveQs.Windows.Services;

public sealed class WpfUserDialogService : IUserDialogService
{
    public void ShowError(string title, Exception exception)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
            MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(owner, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool ConfirmDeleteRange()
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(
            "将永久删除选定日期范围内的本地活动数据。是否继续？",
            "删除本地数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No)
            : MessageBox.Show(
                owner,
                "将永久删除选定日期范围内的本地活动数据。是否继续？",
                "删除本地数据",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public string? SelectExportPath(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出活动数据",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = defaultFileName,
            AddExtension = true,
        };
        var accepted = Application.Current?.MainWindow is { } owner
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();
        return accepted == true ? dialog.FileName : null;
    }
}
