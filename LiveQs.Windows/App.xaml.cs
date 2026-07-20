using System.Windows;
using LiveQs.Windows.App;
using LiveQs.Windows.App.Views;
using LiveQs.Windows.Core;
using LiveQs.Windows.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace LiveQs.Windows;

public partial class LiveQsApplication : System.Windows.Application
{
    private static readonly Uri LightThemeUri = new("App/Styles/Theme.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("App/Styles/Theme.Dark.xaml", UriKind.Relative);
    private IHost? _host;
    private Mutex? _singleInstance;
    private TrayIconService? _trayIcon;
    private MainWindow? _window;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        _singleInstance = new Mutex(true, @"Local\LiveQs.Windows.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("活动时间已经在运行，请从系统托盘打开。", "LiveQs", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            MessageBox.Show(eventArgs.Exception.Message, "LiveQs 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
            eventArgs.Handled = true;
        };

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Services.AddSingleton<IAppPaths, AppPaths>();
            builder.Services.AddSingleton<IActivityRepository, SqliteActivityRepository>();
            builder.Services.AddSingleton<IForegroundSampler, ForegroundSampler>();
            builder.Services.AddSingleton<IStartupManager, StartupManager>();
            builder.Services.AddSingleton<ISyncStatusService, SyncStatusService>();
            builder.Services.AddSingleton<ISyncClient, CloudSyncClient>();
            builder.Services.AddHttpClient("cloud-sync", client => client.Timeout = TimeSpan.FromSeconds(15));
            builder.Services.AddHostedService<SamplingWorker>();
            builder.Services.AddHostedService<SyncWorker>();
            builder.Services.AddHostedService<MaintenanceWorker>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<TrayIconService>();
            _host = builder.Build();

            var repository = _host.Services.GetRequiredService<IActivityRepository>();
            await repository.InitializeAsync();
            await _host.StartAsync();

            _window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = _window;
            _trayIcon = _host.Services.GetRequiredService<TrayIconService>();
            _trayIcon.Initialize(_window);
            var settings = await repository.GetSettingsAsync();
            var background = args.Args.Any(value => string.Equals(value, "--background", StringComparison.OrdinalIgnoreCase));
            if (!background && !settings.StartMinimized) _window.Show();
        }
        catch (Exception exception)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveQs", "Windows");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup-error.log"), $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
            }
            catch (Exception) { }
            MessageBox.Show(exception.ToString(), "LiveQs 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestExit();
        }
    }

    public async void RequestExit()
    {
        if (IsExiting) return;
        IsExiting = true;
        _trayIcon?.Dispose();
        _window?.Close();
        if (_host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _host.StopAsync(timeout.Token); }
            catch (OperationCanceledException) { }
            _host.Dispose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _trayIcon?.Dispose();
        try { _singleInstance?.ReleaseMutex(); }
        catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(args);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) =>
        Dispatcher.Invoke(ApplySystemTheme);

    private void ApplySystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var isLight = key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        var applicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;

        // WPF-UI supplies the base control templates; LiveQs keeps its own semantic palette.
        ApplicationThemeManager.Apply(applicationTheme, Wpf.Ui.Controls.WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(
            isLight ? Color.FromRgb(0, 122, 255) : Color.FromRgb(10, 132, 255),
            applicationTheme,
            false,
            false);

        var desiredSource = isLight ? LightThemeUri : DarkThemeUri;
        var dictionaries = Resources.MergedDictionaries;
        var currentPalette = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            dictionary.Source?.OriginalString.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (currentPalette?.Source?.OriginalString.EndsWith(desiredSource.OriginalString, StringComparison.OrdinalIgnoreCase) == true)
            return;

        var paletteIndex = currentPalette is null ? 0 : dictionaries.IndexOf(currentPalette);
        if (currentPalette is not null) dictionaries.Remove(currentPalette);
        dictionaries.Insert(paletteIndex, new ResourceDictionary { Source = desiredSource });
    }
}
