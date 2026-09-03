using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Publishes the device current state on a fixed heartbeat cadence. The
/// cadence is independent of the sampling interval so the thirty-second
/// freshness guarantee holds for every configuration. Heartbeats are
/// ephemeral assertions, so nothing is queued or persisted locally: a restart
/// heals within one cadence, and a stopped or paused collector goes offline
/// server-side within sixty seconds.
/// </summary>
public sealed class HeartbeatWorker(
    IForegroundSampler sampler,
    ISettingsStore settingsStore,
    IHeartbeatClient client,
    TimeProvider timeProvider,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    /// <summary>Heartbeat cadence; the contract marks a device offline after sixty seconds without one.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try { await Task.Delay(HeartbeatInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    /// One capture-and-publish pass. Failures never break the loop: the next
    /// cadence retries with a freshly captured state.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.GetSettingsAsync(cancellationToken);
            if (!settings.CloudSyncEnabled || settings.SamplingPaused || string.IsNullOrWhiteSpace(settings.DeviceToken)) return;
            var sample = sampler.Capture(settings);
            if (sample is null) return;
            await client.SendAsync(
                new HeartbeatState(sample.CapturedAt, sample.AppId, sample.AppName, sample.IsAfk),
                settings,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Heartbeat upload failed; the next heartbeat will still run.");
        }
    }
}
