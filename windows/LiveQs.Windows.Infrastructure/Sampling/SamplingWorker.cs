using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Sampling;

public sealed class SamplingWorker(
    IForegroundSampler sampler,
    ISettingsStore settingsStore,
    IActivityWriter activityWriter,
    TimeProvider timeProvider,
    ILogger<SamplingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                var settings = await settingsStore.GetSettingsAsync(stoppingToken);
                delay = TimeSpan.FromSeconds(settings.SamplingIntervalSeconds);
                if (!settings.SamplingPaused)
                {
                    var sample = sampler.Capture(settings);
                    if (sample is not null)
                        await activityWriter.RecordSampleAsync(sample, delay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Foreground sampling failed; the next sample will still run.");
            }

            try { await Task.Delay(delay, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
