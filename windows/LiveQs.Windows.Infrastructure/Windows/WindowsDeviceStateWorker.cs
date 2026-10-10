using System.Threading.Channels;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LiveQs.Windows.Infrastructure.Windows;

public sealed class WindowsDeviceStateWorker(IDeviceStateStore store, IWindowsStateEventReader reader,
    WindowsSessionState session, TimeProvider clock, ILogger<WindowsDeviceStateWorker> logger) : BackgroundService
{
    private readonly Channel<DeviceStateEvent> _notifications = Channel.CreateUnbounded<DeviceStateEvent>();
    private readonly string _runId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            if (session.IsLocked)
            {
                Notify(DeviceStateKind.Locked, true, "Windows 锁屏");
            }
            var nextImport = DateTimeOffset.MinValue;
            var notifications = new List<DeviceStateEvent>();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    while (_notifications.Reader.TryRead(out var value))
                    {
                        notifications.Add(value);
                    }
                    await store.AppendDeviceStateEventsAsync(notifications, stoppingToken);
                    notifications.Clear();
                    if (clock.GetUtcNow() >= nextImport)
                    {
                        await ImportAsync(stoppingToken);
                        nextImport = clock.GetUtcNow().AddMinutes(5);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "System state evidence could not be saved; sampling continues.");
                }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        finally
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        var until = clock.GetUtcNow();
        var status = await store.GetDeviceStateImportStatusAsync(cancellationToken);
        var since = status.ImportedUntil?.AddMinutes(-15) ?? until.AddDays(-30);
        try
        {
            var events = await reader.ReadAsync(since, until, cancellationToken);
            await store.CompleteDeviceStateImportAsync(events, until, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Windows system log import is unavailable; existing evidence and sampling remain usable.");
            await store.SetDeviceStateImportWarningAsync("系统日志暂不可读取，部分缺口原因尚未确认；稍后自动重试。", cancellationToken);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
        {
            var locked = args.Reason == SessionSwitchReason.SessionLock;
            session.SetLocked(locked);
            Notify(DeviceStateKind.Locked, locked, "Windows 锁屏");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode is PowerModes.Suspend or PowerModes.Resume)
        {
            Notify(DeviceStateKind.Sleep, args.Mode == PowerModes.Suspend, "睡眠 / 休眠");
        }
    }

    private void Notify(DeviceStateKind kind, bool entering, string description) =>
        _notifications.Writer.TryWrite(new DeviceStateEvent(Guid.NewGuid().ToString("N"), clock.GetUtcNow(), kind,
            entering, $"runtime:{_runId}:{kind}", description));
}
