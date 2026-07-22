using LiveQs.Windows.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure;

public sealed class SamplingWorker(
    IForegroundSampler sampler,
    IActivityRepository repository,
    ILogger<SamplingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                var settings = await repository.GetSettingsAsync(stoppingToken);
                delay = TimeSpan.FromSeconds(settings.SamplingIntervalSeconds);
                if (!settings.SamplingPaused)
                {
                    var sample = sampler.Capture(settings);
                    if (sample is not null)
                        await repository.RecordSampleAsync(sample, delay, stoppingToken);
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

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
