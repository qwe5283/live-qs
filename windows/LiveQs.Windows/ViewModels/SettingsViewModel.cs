using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Core.Update;
using LiveQs.Windows.Services;

namespace LiveQs.Windows.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IActivityQueryService _queryService;
    private readonly ISettingsStore _settingsStore;
    private readonly IActivityMaintenance _maintenance;
    private readonly IStartupManager _startupManager;
    private readonly ISyncStatusService _syncStatusService;
    private readonly IUpdateStatusService _updateStatusService;
    private readonly IAppPaths _paths;
    private readonly IUserDialogService _dialogs;
    private readonly TimeProvider _timeProvider;
    private readonly string _currentVersion;
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
    private string _ownerId = "";
    [ObservableProperty]
    private string _deviceId = "";
    [ObservableProperty]
    private bool _launchOnStartup;
    [ObservableProperty]
    private bool _closeToTray;
    [ObservableProperty]
    private bool _samplingPaused;
    [ObservableProperty]
    private bool _updateCheckEnabled;
    [ObservableProperty]
    private string _updateManifestUrl = "";
    [ObservableProperty]
    private string _statusText = "尚未保存";
    [ObservableProperty]
    private string _syncStatusText = "本地模式";
    [ObservableProperty]
    private string _updateStatusText = "尚未检查";
    [ObservableProperty]
    private string _updateDetailText = "";
    [ObservableProperty]
    private DateTime? _maintenanceStartDate;
    [ObservableProperty]
    private DateTime? _maintenanceEndDate;

    public SettingsViewModel(
        IActivityQueryService queryService,
        ISettingsStore settingsStore,
        IActivityMaintenance maintenance,
        IStartupManager startupManager,
        ISyncStatusService syncStatusService,
        IUpdateStatusService updateStatusService,
        IAppVersion appVersion,
        IAppPaths paths,
        IUserDialogService dialogs,
        TimeProvider timeProvider)
    {
        _queryService = queryService;
        _settingsStore = settingsStore;
        _maintenance = maintenance;
        _startupManager = startupManager;
        _syncStatusService = syncStatusService;
        _updateStatusService = updateStatusService;
        _currentVersion = appVersion.CurrentVersion;
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
        var settings = await _settingsStore.GetSettingsAsync();
        SamplingIntervalSeconds = settings.SamplingIntervalSeconds;
        AfkThresholdSeconds = settings.AfkThresholdSeconds;
        WindowTitleMode = settings.WindowTitleMode;
        RetentionDays = settings.RetentionDays;
        CloudSyncEnabled = settings.CloudSyncEnabled;
        ServerBaseUrl = settings.ServerBaseUrl;
        DeviceToken = settings.DeviceToken;
        OwnerId = settings.OwnerId;
        DeviceId = settings.DeviceId;
        LaunchOnStartup = _startupManager.IsEnabled();
        CloseToTray = settings.CloseToTray;
        SamplingPaused = settings.SamplingPaused;
        UpdateCheckEnabled = settings.UpdateCheckEnabled;
        UpdateManifestUrl = settings.UpdateManifestUrl;
        await ReloadRulesAsync();
        UpdateSyncText(_syncStatusService.Current);
        UpdateUpdateText(_updateStatusService.Current);
        _syncStatusService.Changed -= OnSyncStatusChanged;
        _syncStatusService.Changed += OnSyncStatusChanged;
        _updateStatusService.Changed -= OnUpdateStatusChanged;
        _updateStatusService.Changed += OnUpdateStatusChanged;
        StatusText = "设置已加载";
    }

    private async Task SaveCoreAsync()
    {
        var settings = BuildSettings();
        var validation = settings.Validate();
        if (validation is not null) throw new ArgumentException(validation);
        _startupManager.SetEnabled(LaunchOnStartup);
        await _settingsStore.SaveSettingsAsync(settings);
        foreach (var row in ApplicationRules)
            await _settingsStore.SaveApplicationRuleAsync(row.ToRule());
        StatusText = $"已保存于 {_timeProvider.GetLocalNow():HH:mm:ss}";
    }

    public async Task ReloadRulesAsync()
    {
        var rules = await _queryService.GetApplicationRulesAsync();
        ApplicationRules.Clear();
        foreach (var rule in rules) ApplicationRules.Add(new ApplicationRuleRow(rule));
    }

    public async Task<int> DeleteSelectedRangeAsync()
    {
        var range = SelectedMaintenanceRange();
        var deleted = await _maintenance.DeleteRangeAsync(range);
        StatusText = $"已删除 {deleted} 个时间段";
        return deleted;
    }

    public async Task ExportAsync(string path)
    {
        await _maintenance.ExportCsvAsync(path, SelectedMaintenanceRange());
        StatusText = "CSV 导出完成";
    }

    private async Task OptimizeCoreAsync()
    {
        await _maintenance.OptimizeAsync();
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
        OwnerId = OwnerId,
        DeviceId = DeviceId,
        LaunchOnStartup = LaunchOnStartup,
        CloseToTray = CloseToTray,
        SamplingPaused = SamplingPaused,
        UpdateCheckEnabled = UpdateCheckEnabled,
        UpdateManifestUrl = UpdateManifestUrl,
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

    private void OnUpdateStatusChanged(object? sender, UpdateStatus status) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateUpdateText(status));

    private void UpdateUpdateText(UpdateStatus status)
    {
        UpdateStatusText = !status.Enabled
            ? "更新检查已关闭"
            : status.State switch
            {
                UpdateCheckState.Idle => "尚未检查",
                UpdateCheckState.UpToDate => $"已是最新 · 当前 v{_currentVersion}",
                UpdateCheckState.Available => $"发现新版本 v{status.AvailableVersion}（当前 v{_currentVersion}），安装包已校验",
                UpdateCheckState.Incompatible => $"发现新版本 v{status.AvailableVersion}，但当前 v{_currentVersion} 低于其最低兼容版本，请先手动升级",
                _ => $"检查失败（{status.LastErrorCode}）",
            };
        var details = new List<string>();
        if (status.LastCheckedAt is { } checkedAt) details.Add($"上次检查：{checkedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (status.VerifiedPackagePath is { } packagePath) details.Add($"安装包：{packagePath}");
        if (!string.IsNullOrWhiteSpace(status.LastErrorMessage)) details.Add(status.LastErrorMessage);
        UpdateDetailText = string.Join(Environment.NewLine, details);
    }

    [RelayCommand]
    private void OpenUpdateFolder()
    {
        try
        {
            var directory = Path.Combine(_paths.DataDirectory, "updates");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowError("无法打开目录", exception);
        }
    }

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
