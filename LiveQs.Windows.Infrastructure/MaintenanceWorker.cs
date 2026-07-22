using LiveQs.Windows.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure;

public sealed class MaintenanceWorker(
    IActivityRepository repository,
    TimeProvider timeProvider,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await repository.GetSettingsAsync(stoppingToken);
                var deleted = await repository.DeleteBeforeAsync(timeProvider.GetUtcNow().AddDays(-settings.RetentionDays), stoppingToken);
                await repository.OptimizeAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Retention cleanup deleted {Count} activity segments.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Scheduled local database maintenance failed.");
            }

            try { await Task.Delay(TimeSpan.FromHours(6), timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
