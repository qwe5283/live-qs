using System.Diagnostics;
using System.Drawing;
using LiveQs.Windows.App.Views;
using LiveQs.Windows.App.Controls;
using LiveQs.Windows.Core;
using Forms = System.Windows.Forms;

namespace LiveQs.Windows.App;

public sealed class TrayIconService(IActivityRepository repository, IAppPaths paths) : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private Forms.ToolStripMenuItem? _pauseItem;
    private MainWindow? _window;
    private Icon? _appIcon;

    public void Initialize(MainWindow window)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        var show = new Forms.ToolStripMenuItem("打开活动时间");
        show.Click += (_, _) => ShowWindow();
        _pauseItem = new Forms.ToolStripMenuItem("暂停采样") { CheckOnClick = true };
        _pauseItem.Click += async (_, _) => await TogglePauseAsync();
        var openData = new Forms.ToolStripMenuItem("打开数据目录");
        openData.Click += (_, _) => OpenDataDirectory();
        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(
            () => ((LiveQsApplication)System.Windows.Application.Current).RequestExit());
        menu.Items.AddRange([show, _pauseItem, new Forms.ToolStripSeparator(), openData, new Forms.ToolStripSeparator(), exit]);

        _appIcon = AppIconFactory.Create();
        _icon = new Forms.NotifyIcon
        {
            Text = "LiveQs 活动时间",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _ = RefreshAsync();
    }

    private void ShowWindow() => System.Windows.Application.Current.Dispatcher.Invoke(() => _window?.RestoreAndActivate());

    private async Task TogglePauseAsync()
    {
        if (_pauseItem is null) return;
        try
        {
            var settings = await repository.GetSettingsAsync();
            await repository.SaveSettingsAsync(settings with { SamplingPaused = _pauseItem.Checked });
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _pauseItem.Checked = !_pauseItem.Checked;
            System.Windows.MessageBox.Show(exception.Message, "无法更新采样状态", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        if (_pauseItem is null || _icon is null) return;
        var settings = await repository.GetSettingsAsync();
        _pauseItem.Checked = settings.SamplingPaused;
        _pauseItem.Text = settings.SamplingPaused ? "恢复采样" : "暂停采样";
        _icon.Text = settings.SamplingPaused ? "LiveQs · 采样已暂停" : "LiveQs · 正在采样";
    }

    private void OpenDataDirectory()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{paths.DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { System.Windows.MessageBox.Show(exception.Message, "无法打开目录"); }
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _appIcon?.Dispose();
        _appIcon = null;
        _icon = null;
    }
}
