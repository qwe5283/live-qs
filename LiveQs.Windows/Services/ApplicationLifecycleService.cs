using System.Windows;
using LiveQs.Windows.Core;
using LiveQs.Windows.Views;
using Microsoft.Extensions.Hosting;

namespace LiveQs.Windows.Services;

public sealed class ApplicationLifecycleService : IDisposable
{
    private readonly Application _application;
    private readonly IHost _host;
    private readonly IActivityRepository _repository;
    private readonly TrayIconService _trayIcon;
    private readonly MainWindow _window;
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly Serilog.ILogger _logger;
    private bool _activationPending;
    private bool _started;
    private bool _hostDisposed;
    private bool _disposed;

    public ApplicationLifecycleService(
        Application application,
        IHost host,
        IActivityRepository repository,
        TrayIconService trayIcon,
        MainWindow window,
        SingleInstanceCoordinator singleInstance,
        Serilog.ILogger logger)
    {
        _application = application;
        _host = host;
        _repository = repository;
        _trayIcon = trayIcon;
        _window = window;
        _singleInstance = singleInstance;
        _logger = logger;
        _singleInstance.AttachActivationHandler(HandleActivationRequest);
    }

    public bool IsExiting { get; private set; }

    public async Task StartAsync(bool background)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _repository.InitializeAsync();
        await _host.StartAsync();
        _logger.Information("LiveQs background services started.");

        _application.MainWindow = _window;
        _trayIcon.Initialize(_window);
        _started = true;
        if (_activationPending)
        {
            _activationPending = false;
            _window.RestoreAndActivate();
        }
        else if (!background)
        {
            _window.Show();
        }
    }

    public async Task RequestExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        _logger.Information("LiveQs is shutting down.");
        _trayIcon.Dispose();
        _window.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _host.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { }
        DisposeHost();
        _application.Shutdown();
    }

    private void HandleActivationRequest()
    {
        if (IsExiting) return;
        if (!_started)
        {
            _activationPending = true;
            return;
        }

        _logger.Information("Another process requested activation of the main window.");
        _window.RestoreAndActivate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon.Dispose();
        DisposeHost();
    }

    private void DisposeHost()
    {
        if (_hostDisposed) return;
        _hostDisposed = true;
        _host.Dispose();
    }
}
