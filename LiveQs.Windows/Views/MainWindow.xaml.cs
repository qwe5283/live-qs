using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveQs.Windows.Controls;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IActivityRepository _repository;
    private readonly ISyncStatusService _syncStatusService;
    private readonly DashboardView _dashboard;
    private readonly TimelineView _timeline;
    private readonly SettingsView _settings;
    private readonly DispatcherTimer _statusTimer;
    private bool _closeToTray = true;

    public MainWindow(
        IActivityRepository repository,
        ISyncStatusService syncStatusService,
        DashboardView dashboard,
        TimelineView timeline,
        SettingsView settings)
    {
        InitializeComponent();
        Icon = AppIconFactory.CreateApplicationIcon();
        _repository = repository;
        _syncStatusService = syncStatusService;
        _dashboard = dashboard;
        _timeline = timeline;
        _settings = settings;
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, OnStatusTick, Dispatcher);
        _syncStatusService.Changed += OnSyncStatusChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        Loaded += OnLoaded;
        SelectPage("statistics");
    }

    public void RestoreAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        await RefreshStatusAsync();
        _statusTimer.Start();
    }

    private async void Navigation_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string page }) return;
        SelectPage(page);
        try
        {
            if (page == "statistics") await _dashboard.RefreshAsync();
            else if (page == "timeline") await _timeline.RefreshAsync();
            else await _settings.RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "刷新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectPage(string page)
    {
        var pageTitle = page switch
        {
            "timeline" => "时间线",
            "settings" => "设置",
            _ => "统计",
        };

        PageContent.Content = page switch
        {
            "timeline" => _timeline,
            "settings" => _settings,
            _ => _dashboard,
        };
        AppTitleBar.Title = pageTitle;
        Title = $"{pageTitle} - 活动时间";
        SetSelected(StatisticsButton, page == "statistics");
        SetSelected(TimelineButton, page == "timeline");
        SetSelected(SettingsButton, page == "settings");
    }

    private static void SetSelected(Button button, bool selected)
    {
        if (selected)
        {
            button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SidebarNavSelectedBrush");
            button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "SidebarNavSelectedTextBrush");
            return;
        }

        button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        button.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
    }

    private async void OnStatusTick(object? sender, EventArgs args) => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        try
        {
            var settings = await _repository.GetSettingsAsync();
            _closeToTray = settings.CloseToTray;
            SamplingStatusText.Text = settings.SamplingPaused ? "采样已暂停" : "采样中";
            SamplingDot.SetResourceReference(
                System.Windows.Shapes.Shape.FillProperty,
                settings.SamplingPaused ? "WarningBrush" : "SidebarSamplingActiveBrush");
            ApplySyncStatus(_syncStatusService.Current);
        }
        catch (Exception)
        {
            SamplingStatusText.Text = "状态未知";
            SamplingDot.Fill = System.Windows.Media.Brushes.Gray;
        }
    }

    private void OnSyncStatusChanged(object? sender, SyncStatus status) => Dispatcher.Invoke(() => ApplySyncStatus(status));

    private void ApplySyncStatus(SyncStatus status)
    {
        SyncStatusText.Text = !status.Enabled
            ? $"本地模式 · {status.PendingCount} 待同步"
            : status.IsRunning ? $"正在同步 · {status.PendingCount} 条" :
            string.IsNullOrWhiteSpace(status.LastError) ? $"云端同步 · {status.PendingCount} 条" : "云端暂不可用";
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        var app = (LiveQsApplication)System.Windows.Application.Current;
        if (app.IsExiting) return;
        args.Cancel = true;
        if (_closeToTray) Hide();
        else Dispatcher.BeginInvoke(app.RequestExit);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _statusTimer.Stop();
        _syncStatusService.Changed -= OnSyncStatusChanged;
    }
}
