using System.Windows;
using LiveQs.Windows.Core;
using LiveQs.Windows.Infrastructure;
using LiveQs.Windows.Services;
using LiveQs.Windows.ViewModels;
using LiveQs.Windows.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;
using Wpf.Ui.Appearance;

namespace LiveQs.Windows;

public partial class LiveQsApplication : System.Windows.Application
{
    private static readonly Uri LightThemeUri = new("Styles/Theme.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Styles/Theme.Dark.xaml", UriKind.Relative);
    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;
    private ApplicationLifecycleService? _lifecycle;
    private Serilog.ILogger? _bootstrapLogger;

    public bool IsExiting => _lifecycle?.IsExiting == true;

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        _singleInstance = new SingleInstanceCoordinator(Dispatcher);
        if (!_singleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            _bootstrapLogger?.Error(eventArgs.Exception, "Unhandled dispatcher exception.");
            MessageBox.Show(eventArgs.Exception.Message, "LiveQs 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
            eventArgs.Handled = true;
        };

        try
        {
            var builder = Host.CreateApplicationBuilder();
            var appPaths = new AppPaths();
            _bootstrapLogger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    appPaths.LogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true)
                .CreateLogger();
            _bootstrapLogger.Information("LiveQs is starting.");
            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Logging.AddSerilog(_bootstrapLogger, dispose: false);
            builder.Services.AddLiveQsInfrastructure(appPaths);
            builder.Services.AddSingleton<IUserDialogService, WpfUserDialogService>();
            builder.Services.AddSingleton<DashboardViewModel>();
            builder.Services.AddSingleton<TimelineViewModel>();
            builder.Services.AddSingleton<SettingsViewModel>();
            builder.Services.AddSingleton<DashboardView>();
            builder.Services.AddSingleton<TimelineView>();
            builder.Services.AddSingleton<SettingsView>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<TrayIconService>();
            _host = builder.Build();
            _lifecycle = new ApplicationLifecycleService(
                this,
                _host,
                _host.Services.GetRequiredService<IActivityRepository>(),
                _host.Services.GetRequiredService<TrayIconService>(),
                _host.Services.GetRequiredService<MainWindow>(),
                _singleInstance,
                _bootstrapLogger);
            var background = args.Args.Any(value => string.Equals(value, "--background", StringComparison.OrdinalIgnoreCase));
            await _lifecycle.StartAsync(background);
        }
        catch (Exception exception)
        {
            _bootstrapLogger?.Fatal(exception, "LiveQs failed to start.");
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveQs", "Windows");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup-error.log"), $"[{TimeProvider.System.GetLocalNow():O}]\n{exception}\n\n");
            }
            catch (Exception) { }
            MessageBox.Show(exception.ToString(), "LiveQs 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestExit();
        }
    }

    public async void RequestExit()
    {
        if (_lifecycle is not null) await _lifecycle.RequestExitAsync();
        else Shutdown();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _lifecycle?.Dispose();
        _lifecycle = null;
        if (_host is not null)
        {
            try { _host.Dispose(); }
            catch (ObjectDisposedException) { }
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        _host = null;
        (_bootstrapLogger as IDisposable)?.Dispose();
        _bootstrapLogger = null;
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
