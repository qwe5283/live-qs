using System.Collections.ObjectModel;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.App.ViewModels;

public sealed class SettingsViewModel(
    IActivityRepository repository,
    IStartupManager startupManager,
    ISyncStatusService syncStatusService,
    IAppPaths paths) : ViewModelBase
{
    private int _samplingIntervalSeconds;
    private int _afkThresholdSeconds;
    private WindowTitleMode _windowTitleMode;
    private int _retentionDays;
    private bool _cloudSyncEnabled;
    private string _serverBaseUrl = "";
    private string _deviceToken = "";
    private string _deviceId = "";
    private bool _launchOnStartup;
    private bool _startMinimized;
    private bool _closeToTray;
    private bool _samplingPaused;
    private string _statusText = "尚未保存";
    private string _syncStatusText = "本地模式";
    private DateTime? _maintenanceStartDate = DateTime.Today;
    private DateTime? _maintenanceEndDate = DateTime.Today;

    public SettingsViewModel() : this(null!, null!, null!, null!) { }

    public IReadOnlyList<WindowTitleMode> WindowTitleModes { get; } = Enum.GetValues<WindowTitleMode>();
    public ObservableCollection<ApplicationRuleRow> ApplicationRules { get; } = [];
    public string DatabasePath => paths?.DatabasePath ?? "";
    public int SamplingIntervalSeconds { get => _samplingIntervalSeconds; set => Set(ref _samplingIntervalSeconds, value); }
    public int AfkThresholdSeconds { get => _afkThresholdSeconds; set => Set(ref _afkThresholdSeconds, value); }
    public WindowTitleMode WindowTitleMode { get => _windowTitleMode; set => Set(ref _windowTitleMode, value); }
    public int RetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }
    public bool CloudSyncEnabled { get => _cloudSyncEnabled; set => Set(ref _cloudSyncEnabled, value); }
    public string ServerBaseUrl { get => _serverBaseUrl; set => Set(ref _serverBaseUrl, value); }
    public string DeviceToken { get => _deviceToken; set => Set(ref _deviceToken, value); }
    public string DeviceId { get => _deviceId; set => Set(ref _deviceId, value); }
    public bool LaunchOnStartup { get => _launchOnStartup; set => Set(ref _launchOnStartup, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    public bool SamplingPaused { get => _samplingPaused; set => Set(ref _samplingPaused, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string SyncStatusText { get => _syncStatusText; private set => Set(ref _syncStatusText, value); }
    public DateTime? MaintenanceStartDate { get => _maintenanceStartDate; set => Set(ref _maintenanceStartDate, value); }
    public DateTime? MaintenanceEndDate { get => _maintenanceEndDate; set => Set(ref _maintenanceEndDate, value); }

    public async Task LoadAsync()
    {
        var settings = await repository.GetSettingsAsync();
        SamplingIntervalSeconds = settings.SamplingIntervalSeconds;
        AfkThresholdSeconds = settings.AfkThresholdSeconds;
        WindowTitleMode = settings.WindowTitleMode;
        RetentionDays = settings.RetentionDays;
        CloudSyncEnabled = settings.CloudSyncEnabled;
        ServerBaseUrl = settings.ServerBaseUrl;
        DeviceToken = settings.DeviceToken;
        DeviceId = settings.DeviceId;
        LaunchOnStartup = startupManager.IsEnabled();
        StartMinimized = settings.StartMinimized;
        CloseToTray = settings.CloseToTray;
        SamplingPaused = settings.SamplingPaused;
        await ReloadRulesAsync();
        UpdateSyncText(syncStatusService.Current);
        syncStatusService.Changed -= OnSyncStatusChanged;
        syncStatusService.Changed += OnSyncStatusChanged;
        StatusText = "设置已加载";
    }

    public async Task SaveAsync()
    {
        var settings = BuildSettings();
        var validation = settings.Validate();
        if (validation is not null) throw new ArgumentException(validation);
        startupManager.SetEnabled(LaunchOnStartup);
        await repository.SaveSettingsAsync(settings);
        foreach (var row in ApplicationRules)
            await repository.SaveApplicationRuleAsync(row.ToRule());
        StatusText = $"已保存于 {DateTime.Now:HH:mm:ss}";
    }

    public async Task ReloadRulesAsync()
    {
        var rules = await repository.GetApplicationRulesAsync();
        ApplicationRules.Clear();
        foreach (var rule in rules) ApplicationRules.Add(new ApplicationRuleRow(rule));
    }

    public async Task<int> DeleteSelectedRangeAsync()
    {
        var range = SelectedMaintenanceRange();
        var deleted = await repository.DeleteRangeAsync(range);
        StatusText = $"已删除 {deleted} 个时间段";
        return deleted;
    }

    public async Task ExportAsync(string path)
    {
        await repository.ExportCsvAsync(path, SelectedMaintenanceRange());
        StatusText = "CSV 导出完成";
    }

    public async Task OptimizeAsync()
    {
        await repository.OptimizeAsync();
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
        StartMinimized = StartMinimized,
        CloseToTray = CloseToTray,
        SamplingPaused = SamplingPaused,
    }.Normalize();

    private DateRange SelectedMaintenanceRange()
    {
        var start = (MaintenanceStartDate ?? DateTime.Today).Date;
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
}

public sealed class ApplicationRuleRow : ViewModelBase
{
    private string _alias;
    private string _category;
    private bool _isExcluded;

    public ApplicationRuleRow(ApplicationRule rule)
    {
        AppId = rule.AppId;
        _alias = rule.Alias;
        _category = rule.Category;
        _isExcluded = rule.IsExcluded;
    }

    public string AppId { get; }
    public string Alias { get => _alias; set => Set(ref _alias, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public bool IsExcluded { get => _isExcluded; set => Set(ref _isExcluded, value); }
    public ApplicationRule ToRule() => new(AppId, Alias ?? "", Category ?? "未分类", IsExcluded);
}
