using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core;
using LiveQs.Windows.Services;

namespace LiveQs.Windows.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IActivityRepository _repository;
    private readonly IStartupManager _startupManager;
    private readonly ISyncStatusService _syncStatusService;
    private readonly IAppPaths _paths;
    private readonly IUserDialogService _dialogs;
    private readonly TimeProvider _timeProvider;
    [ObservableProperty]
    private int _samplingIntervalSeconds;
    [ObservableProperty]
    private int _afkThresholdSeconds;
    [ObservableProperty]
    private WindowTitleMode _windowTitleMode;
    [ObservableProperty]
    private int _retentionDays;
    [ObservableProperty]
    private bool _cloudSyncEnabled;
    [ObservableProperty]
    private string _serverBaseUrl = "";
    [ObservableProperty]
    private string _deviceToken = "";
    [ObservableProperty]
    private string _deviceId = "";
    [ObservableProperty]
    private bool _launchOnStartup;
    [ObservableProperty]
    private bool _closeToTray;
    [ObservableProperty]
    private bool _samplingPaused;
    [ObservableProperty]
    private string _statusText = "尚未保存";
    [ObservableProperty]
    private string _syncStatusText = "本地模式";
    [ObservableProperty]
    private DateTime? _maintenanceStartDate;
    [ObservableProperty]
    private DateTime? _maintenanceEndDate;

    public SettingsViewModel(
        IActivityRepository repository,
        IStartupManager startupManager,
        ISyncStatusService syncStatusService,
        IAppPaths paths,
        IUserDialogService dialogs,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _startupManager = startupManager;
        _syncStatusService = syncStatusService;
        _paths = paths;
        _dialogs = dialogs;
        _timeProvider = timeProvider;
        _maintenanceStartDate = Today;
        _maintenanceEndDate = Today;
    }

    public IReadOnlyList<WindowTitleMode> WindowTitleModes { get; } = Enum.GetValues<WindowTitleMode>();
    public ObservableCollection<ApplicationRuleRow> ApplicationRules { get; } = [];
    public string DatabasePath => _paths.DatabasePath;

    public async Task LoadAsync()
    {
        var settings = await _repository.GetSettingsAsync();
        SamplingIntervalSeconds = settings.SamplingIntervalSeconds;
        AfkThresholdSeconds = settings.AfkThresholdSeconds;
        WindowTitleMode = settings.WindowTitleMode;
        RetentionDays = settings.RetentionDays;
        CloudSyncEnabled = settings.CloudSyncEnabled;
        ServerBaseUrl = settings.ServerBaseUrl;
        DeviceToken = settings.DeviceToken;
        DeviceId = settings.DeviceId;
        LaunchOnStartup = _startupManager.IsEnabled();
        CloseToTray = settings.CloseToTray;
        SamplingPaused = settings.SamplingPaused;
        await ReloadRulesAsync();
        UpdateSyncText(_syncStatusService.Current);
        _syncStatusService.Changed -= OnSyncStatusChanged;
        _syncStatusService.Changed += OnSyncStatusChanged;
        StatusText = "设置已加载";
    }

    private async Task SaveCoreAsync()
    {
        var settings = BuildSettings();
        var validation = settings.Validate();
        if (validation is not null) throw new ArgumentException(validation);
        _startupManager.SetEnabled(LaunchOnStartup);
        await _repository.SaveSettingsAsync(settings);
        foreach (var row in ApplicationRules)
            await _repository.SaveApplicationRuleAsync(row.ToRule());
        StatusText = $"已保存于 {_timeProvider.GetLocalNow():HH:mm:ss}";
    }

    public async Task ReloadRulesAsync()
    {
        var rules = await _repository.GetApplicationRulesAsync();
        ApplicationRules.Clear();
        foreach (var rule in rules) ApplicationRules.Add(new ApplicationRuleRow(rule));
    }

    public async Task<int> DeleteSelectedRangeAsync()
    {
        var range = SelectedMaintenanceRange();
        var deleted = await _repository.DeleteRangeAsync(range);
        StatusText = $"已删除 {deleted} 个时间段";
        return deleted;
    }

    public async Task ExportAsync(string path)
    {
        await _repository.ExportCsvAsync(path, SelectedMaintenanceRange());
        StatusText = "CSV 导出完成";
    }

    private async Task OptimizeCoreAsync()
    {
        await _repository.OptimizeAsync();
        StatusText = "数据库维护完成";
    }

    private AppSettings BuildSettings() => new AppSettings
    {
        SamplingIntervalSeconds = SamplingIntervalSeconds,
        AfkThresholdSeconds = AfkThresholdSeconds,
        WindowTitleMode = WindowTitleMode,
        RetentionDays = RetentionDays,
        CloudSyncEnabled = CloudSyncEnabled,
        ServerBaseUrl = ServerBaseUrl,
        DeviceToken = DeviceToken,
        DeviceId = DeviceId,
        LaunchOnStartup = LaunchOnStartup,
        CloseToTray = CloseToTray,
        SamplingPaused = SamplingPaused,
    }.Normalize();

    private DateRange SelectedMaintenanceRange()
    {
        var start = (MaintenanceStartDate ?? Today).Date;
        var end = (MaintenanceEndDate ?? start).Date;
        if (end < start) (start, end) = (end, start);
        return DateRange.FromLocalDates(start, end);
    }

    private void OnSyncStatusChanged(object? sender, SyncStatus status) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateSyncText(status));

    private void UpdateSyncText(SyncStatus status)
    {
        SyncStatusText = !status.Enabled
            ? $"本地模式 · {status.PendingCount} 条待同步"
            : status.IsRunning
                ? $"正在同步 · {status.PendingCount} 条待处理"
                : string.IsNullOrWhiteSpace(status.LastError)
                    ? $"云端已连接 · {status.PendingCount} 条待处理"
                    : $"云端暂不可用 · {status.PendingCount} 条待处理";
    }

    private async Task ExportSelectedRangeAsync()
    {
        var path = _dialogs.SelectExportPath($"LiveQs-{Today:yyyy-MM-dd}.csv");
        if (path is not null) await RunAsync(() => ExportAsync(path), "导出失败");
    }

    private async Task DeleteSelectedRangeWithConfirmationAsync()
    {
        if (_dialogs.ConfirmDeleteRange())
            await RunAsync(async () => { _ = await DeleteSelectedRangeAsync(); }, "删除失败");
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(SaveCoreAsync, "保存设置失败");

    [RelayCommand]
    private Task ExportAsync() => ExportSelectedRangeAsync();

    [RelayCommand]
    private Task DeleteAsync() => DeleteSelectedRangeWithConfirmationAsync();

    [RelayCommand]
    private Task OptimizeAsync() => RunAsync(OptimizeCoreAsync, "数据库维护失败");

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowError("无法打开目录", exception);
        }
    }

    private DateTime Today => _timeProvider.GetLocalNow().Date;

    private async Task RunAsync(Func<Task> action, string title)
    {
        try { await action(); }
        catch (Exception exception) { _dialogs.ShowError(title, exception); }
    }
}

public sealed partial class ApplicationRuleRow : ViewModelBase
{
    [ObservableProperty]
    private string _alias;
    [ObservableProperty]
    private string _category;
    [ObservableProperty]
    private bool _isExcluded;

    public ApplicationRuleRow(ApplicationRule rule)
    {
        AppId = rule.AppId;
        _alias = rule.Alias;
        _category = rule.Category;
        _isExcluded = rule.IsExcluded;
    }

    public string AppId { get; }
    public ApplicationRule ToRule() => new(AppId, Alias ?? "", Category ?? "未分类", IsExcluded);
}
