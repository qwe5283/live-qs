using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LiveQs.Windows.Controls;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Core.Update;
using LiveQs.Windows.Interop;
using LiveQs.Windows.Views;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Tray.Controls;

namespace LiveQs.Windows.Services;

public sealed class TrayIconService(
    ISettingsStore settingsStore,
    IAppPaths paths,
    ISyncStatusService syncStatusService,
    IUpdateStatusService updateStatusService,
    ILogger<TrayIconService> logger) : IDisposable
{
    private NotifyIcon? _icon;
    private MenuItem? _pauseItem;
    private MainWindow? _window;
    private Window? _trayHostWindow;
    private DispatcherTimer? _refreshTimer;
    private TrayIconState? _currentIconState;
    private string? _currentTooltip;
    private bool _samplingPaused;
    private bool _cloudSyncEnabled;
    private bool _updateAvailable;
    private bool _isRefreshing;

    public void Initialize(MainWindow window)
    {
        _window = window;
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "打开主界面" };
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

        var trayHostWindow = new Window
        {
            Width = 0,
            Height = 0,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Opacity = 0,
        };
        trayHostWindow.SourceInitialized += (_, _) => ConfigureTrayHostWindow(trayHostWindow);
        _trayHostWindow = trayHostWindow;
        trayHostWindow.Show();

        _icon = new NotifyIcon
        {
            TooltipText = "LiveQs 活动时间",
            Icon = AppIconFactory.CreateTrayIcon(TrayIconState.Local),
            Menu = menu,
        };
        var application = System.Windows.Application.Current;
        var mainWindow = application.MainWindow;
        application.MainWindow = _trayHostWindow;
        try { _icon.Register(); }
        finally { application.MainWindow = mainWindow; }
        if (!_icon.IsRegistered) logger.LogWarning("The tray icon could not be registered with Windows Explorer.");

        syncStatusService.Changed += OnSyncStatusChanged;
        updateStatusService.Changed += OnUpdateStatusChanged;
        _updateAvailable = updateStatusService.Current.State == UpdateCheckState.Available;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, OnRefreshTimer, application.Dispatcher);
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    private void ShowWindow() => System.Windows.Application.Current.Dispatcher.Invoke(() => _window?.RestoreAndActivate());

    private async Task TogglePauseAsync()
    {
        if (_pauseItem is null) return;
        try
        {
            var settings = await settingsStore.GetSettingsAsync();
            await settingsStore.SaveSettingsAsync(settings with { SamplingPaused = _pauseItem.IsChecked });
            _samplingPaused = _pauseItem.IsChecked;
            ApplyTrayState(syncStatusService.Current);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _pauseItem.IsChecked = !_pauseItem.IsChecked;
            _samplingPaused = _pauseItem.IsChecked;
            ApplyTrayState(syncStatusService.Current);
            System.Windows.MessageBox.Show(exception.Message, "无法更新采样状态", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        if (_pauseItem is null || _icon is null || _isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var settings = await settingsStore.GetSettingsAsync();
            _samplingPaused = settings.SamplingPaused;
            _cloudSyncEnabled = settings.CloudSyncEnabled;
            _updateAvailable = updateStatusService.Current.State == UpdateCheckState.Available;
            _pauseItem.IsChecked = _samplingPaused;
            _pauseItem.Header = _samplingPaused ? "恢复采样" : "暂停采样";
            ApplyTrayState(syncStatusService.Current);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The tray status could not be refreshed.");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async void OnRefreshTimer(object? sender, EventArgs args) => await RefreshAsync();

    private void OnSyncStatusChanged(object? sender, SyncStatus status) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyTrayState(status));

    private void OnUpdateStatusChanged(object? sender, UpdateStatus status) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _updateAvailable = status.State == UpdateCheckState.Available;
            ApplyTrayState(syncStatusService.Current);
        });

    private void ApplyTrayState(SyncStatus syncStatus)
    {
        if (_icon is null) return;

        var state = ResolveIconState(_samplingPaused, _cloudSyncEnabled, syncStatus.LastError, _updateAvailable);
        var tooltip = state switch
        {
            TrayIconState.Paused => "LiveQs · 采样已暂停",
            TrayIconState.CloudConnected => "LiveQs · 正在采样 · 云同步",
            TrayIconState.CloudUnavailable => "LiveQs · 正在采样 · 云端不可达",
            TrayIconState.UpdateAvailable => "LiveQs · 发现新版本 · 详见设置",
            _ => "LiveQs · 正在采样 · 本地模式",
        };

        if (_currentIconState != state)
        {
            _icon.Icon = AppIconFactory.CreateTrayIcon(state);
            _currentIconState = state;
        }
        if (!string.Equals(_currentTooltip, tooltip, StringComparison.Ordinal))
        {
            _icon.TooltipText = tooltip;
            _currentTooltip = tooltip;
        }
    }

    internal static TrayIconState ResolveIconState(
        bool samplingPaused, bool cloudSyncEnabled, string? syncError, bool updateAvailable) =>
        samplingPaused
            ? TrayIconState.Paused
            : !cloudSyncEnabled
                ? updateAvailable ? TrayIconState.UpdateAvailable : TrayIconState.Local
                : string.IsNullOrWhiteSpace(syncError)
                    ? updateAvailable ? TrayIconState.UpdateAvailable : TrayIconState.CloudConnected
                    : TrayIconState.CloudUnavailable;

    private void OpenDataDirectory()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{paths.DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { System.Windows.MessageBox.Show(exception.Message, "无法打开目录"); }
    }

    private static void ConfigureTrayHostWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var extendedStyle = TrayWindowNativeMethods.GetWindowLong(handle, TrayWindowNativeMethods.GwlExStyle);
        _ = TrayWindowNativeMethods.SetWindowLong(
            handle,
            TrayWindowNativeMethods.GwlExStyle,
            extendedStyle | TrayWindowNativeMethods.WsExToolWindow | TrayWindowNativeMethods.WsExNoActivate);
    }

    public void Dispose()
    {
        if (_icon is null) return;
        syncStatusService.Changed -= OnSyncStatusChanged;
        updateStatusService.Changed -= OnUpdateStatusChanged;
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _icon.Unregister();
        _icon.Dispose();
        _icon = null;
        _trayHostWindow?.Close();
        _trayHostWindow = null;
    }
}
