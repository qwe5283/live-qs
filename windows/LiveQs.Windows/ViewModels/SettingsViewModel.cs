using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
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
        if (validation is not null)
        {
            throw new ArgumentException(validation);
        }

        _startupManager.SetEnabled(LaunchOnStartup);
        await _settingsStore.SaveSettingsAsync(settings);
        foreach (var row in ApplicationRules)
        {
            await _settingsStore.SaveApplicationRuleAsync(row.ToRule());
        }

        StatusText = $"已保存于 {_timeProvider.GetLocalNow():HH:mm:ss}";
    }

    public async Task ReloadRulesAsync()
    {
        var rules = await _queryService.GetApplicationRulesAsync();
        ApplicationRules.Clear();
        foreach (var rule in rules)
        {
            ApplicationRules.Add(new ApplicationRuleRow(rule));
        }
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

    [RelayCommand]
    private Task SaveAsync() => RunAsync(SaveCoreAsync, "保存设置失败");

    private DateTime Today => _timeProvider.GetLocalNow().Date;

    private async Task RunAsync(Func<Task> action, string title)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _dialogs.ShowError(title, exception);
        }
    }
}
