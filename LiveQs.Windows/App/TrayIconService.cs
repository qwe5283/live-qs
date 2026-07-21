using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LiveQs.Windows.App.Views;
using LiveQs.Windows.App.Controls;
using LiveQs.Windows.Core;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Tray;

namespace LiveQs.Windows.App;

public sealed class TrayIconService(
    IActivityRepository repository,
    IAppPaths paths,
    ILogger<TrayIconService> logger) : IDisposable
{
    private NotifyIconService? _icon;
    private MenuItem? _pauseItem;
    private MainWindow? _window;
    private Window? _trayHostWindow;

    public void Initialize(MainWindow window)
    {
        _window = window;
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "打开活动时间" };
        show.Click += (_, _) => ShowWindow();
        _pauseItem = new MenuItem { Header = "暂停采样", IsCheckable = true };
        _pauseItem.Click += async (_, _) => await TogglePauseAsync();
        var openData = new MenuItem { Header = "打开数据目录" };
        openData.Click += (_, _) => OpenDataDirectory();
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(
            () => ((LiveQsApplication)System.Windows.Application.Current).RequestExit());
        menu.Items.Add(show);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openData);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _trayHostWindow = new Window
        {
            Width = 0,
            Height = 0,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Opacity = 0,
        };
        _trayHostWindow.Show();

        _icon = new NotifyIconService
        {
            TooltipText = "LiveQs 活动时间",
            Icon = AppIconFactory.CreateImageSource(),
            ContextMenu = menu,
        };
        _icon.SetParentWindow(_trayHostWindow);
        if (!_icon.Register()) logger.LogWarning("The tray icon could not be registered with Windows Explorer.");
        _ = RefreshAsync();
    }

    private void ShowWindow() => System.Windows.Application.Current.Dispatcher.Invoke(() => _window?.RestoreAndActivate());

    private async Task TogglePauseAsync()
    {
        if (_pauseItem is null) return;
        try
        {
            var settings = await repository.GetSettingsAsync();
            await repository.SaveSettingsAsync(settings with { SamplingPaused = _pauseItem.IsChecked });
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _pauseItem.IsChecked = !_pauseItem.IsChecked;
            System.Windows.MessageBox.Show(exception.Message, "无法更新采样状态", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        if (_pauseItem is null || _icon is null) return;
        try
        {
            var settings = await repository.GetSettingsAsync();
            _pauseItem.IsChecked = settings.SamplingPaused;
            _pauseItem.Header = settings.SamplingPaused ? "恢复采样" : "暂停采样";
            _icon.TooltipText = settings.SamplingPaused ? "LiveQs · 采样已暂停" : "LiveQs · 正在采样";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The tray status could not be refreshed.");
        }
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
        _icon.Unregister();
        _icon = null;
        _trayHostWindow?.Close();
        _trayHostWindow = null;
    }
}
