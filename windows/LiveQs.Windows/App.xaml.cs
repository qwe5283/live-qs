using System.Windows;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Infrastructure.Configuration;
using LiveQs.Windows.Infrastructure.DependencyInjection;
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
    private static readonly Uri _lightThemeUri = new("Styles/Theme.Light.xaml", UriKind.Relative);
    private static readonly Uri _darkThemeUri = new("Styles/Theme.Dark.xaml", UriKind.Relative);
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
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var appPaths = new AppPaths();
            _bootstrapLogger = CreateBootstrapLogger(appPaths);
            _bootstrapLogger.Information("LiveQs is starting.");
            _host = BuildHost(appPaths, _bootstrapLogger);
            _lifecycle = new ApplicationLifecycleService(
                this,
                _host,
                _host.Services.GetRequiredService<IDatabaseInitializer>(),
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
            WriteStartupFailure(exception);
            MessageBox.Show(exception.ToString(), "LiveQs 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestExit();
        }
    }

    private static Serilog.ILogger CreateBootstrapLogger(IAppPaths paths) => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.File(
            paths.LogPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                "{Message:lj} {Properties:j}{NewLine}{Exception}")
        .CreateLogger();

    private static IHost BuildHost(IAppPaths paths, Serilog.ILogger logger)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddSerilog(logger, dispose: false);
        builder.Services.AddLiveQsInfrastructure(paths);
        builder.Services.AddSingleton<IUserDialogService, WpfUserDialogService>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<TimelineViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<DashboardView>();
        builder.Services.AddSingleton<TimelineView>();
        builder.Services.AddSingleton<SettingsView>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIconService>();
        return builder.Build();
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        _bootstrapLogger?.Error(args.Exception, "Unhandled dispatcher exception.");
        MessageBox.Show(args.Exception.Message, "LiveQs 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    private static void WriteStartupFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveQs", "Windows");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup-error.log"),
                $"[{TimeProvider.System.GetLocalNow():O}]\n{exception}\n\n");
        }
        catch (Exception)
        {
            // Startup can fail before the logger exists, including when the data directory is unwritable.
            // Preserve the original exception for the error dialog.
        }
    }

    public async void RequestExit()
    {
        if (_lifecycle is not null)
        {
            await _lifecycle.RequestExitAsync();
        }
        else
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs args)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        _lifecycle?.Dispose();
        _lifecycle = null;
        if (_host is not null)
        {
            try
            {
                _host.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
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

        var desiredSource = isLight ? _lightThemeUri : _darkThemeUri;
        var dictionaries = Resources.MergedDictionaries;
        var currentPalette = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            dictionary.Source?.OriginalString.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (currentPalette?.Source?.OriginalString.EndsWith(desiredSource.OriginalString, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var paletteIndex = currentPalette is null ? 0 : dictionaries.IndexOf(currentPalette);
        if (currentPalette is not null)
        {
            dictionaries.Remove(currentPalette);
        }

        dictionaries.Insert(paletteIndex, new ResourceDictionary { Source = desiredSource });
    }
}
