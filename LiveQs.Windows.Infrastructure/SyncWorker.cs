using LiveQs.Windows.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure;

public sealed class SyncWorker(
    IActivityRepository repository,
    ISyncClient client,
    ISyncStatusService statusService,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastSuccess = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await repository.GetSettingsAsync(stoppingToken);
                var pending = await repository.GetPendingSyncCountAsync(stoppingToken);
                if (!settings.CloudSyncEnabled)
                {
                    statusService.Update(new SyncStatus(false, false, pending, lastSuccess, ""));
                    await Delay(stoppingToken, TimeSpan.FromSeconds(15));
                    continue;
                }

                var items = await repository.GetPendingSyncAsync(100, DateTimeOffset.UtcNow, stoppingToken);
                if (items.Count == 0)
                {
                    statusService.Update(new SyncStatus(true, false, pending, lastSuccess, ""));
                    await Delay(stoppingToken, TimeSpan.FromSeconds(10));
                    continue;
                }

                statusService.Update(new SyncStatus(true, true, pending, lastSuccess, ""));
                try
                {
                    await client.UploadAsync(items, settings, stoppingToken);
                    lastSuccess = DateTimeOffset.UtcNow;
                    await repository.MarkSyncedAsync(items.Select(item => item.SegmentId), lastSuccess.Value, stoppingToken);
                    pending = await repository.GetPendingSyncCountAsync(stoppingToken);
                    statusService.Update(new SyncStatus(true, false, pending, lastSuccess, ""));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    var attempt = items.Max(item => item.AttemptCount) + 1;
                    var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(attempt, 8)));
                    var next = DateTimeOffset.UtcNow.AddSeconds(seconds + Random.Shared.Next(0, 10));
                    await repository.MarkSyncFailedAsync(items.Select(item => item.SegmentId), exception.Message, next, stoppingToken);
                    logger.LogWarning(exception, "Cloud sync failed; local collection remains active.");
                    statusService.Update(new SyncStatus(true, false, pending, lastSuccess, exception.Message));
                    await Delay(stoppingToken, TimeSpan.FromSeconds(10));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Sync worker iteration failed.");
                await Delay(stoppingToken, TimeSpan.FromSeconds(15));
            }
        }
    }

    private static async Task Delay(CancellationToken token, TimeSpan duration)
    {
        try { await Task.Delay(duration, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
