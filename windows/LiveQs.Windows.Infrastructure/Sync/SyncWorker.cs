using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Sync;

public sealed class SyncWorker(
    ISettingsStore settingsStore,
    ISyncQueueStore syncQueue,
    ISyncClient client,
    ISyncStatusService statusService,
    TimeProvider timeProvider,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastSuccess = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await settingsStore.GetSettingsAsync(stoppingToken);
                var pending = await syncQueue.GetPendingSyncCountAsync(stoppingToken);
                if (!settings.CloudSyncEnabled)
                {
                    statusService.Update(new SyncStatus(false, false, pending, lastSuccess, ""));
                    await Delay(stoppingToken, TimeSpan.FromSeconds(15));
                    continue;
                }

                var items = await syncQueue.GetPendingSyncAsync(100, timeProvider.GetUtcNow(), stoppingToken);
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
                    lastSuccess = timeProvider.GetUtcNow();
                    await syncQueue.MarkSyncedAsync(items.Select(item => item.SegmentId), lastSuccess.Value, stoppingToken);
                    pending = await syncQueue.GetPendingSyncCountAsync(stoppingToken);
                    statusService.Update(new SyncStatus(true, false, pending, lastSuccess, ""));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    var attempt = items.Max(item => item.AttemptCount) + 1;
                    var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(attempt, 8)));
                    var next = timeProvider.GetUtcNow().AddSeconds(seconds + Random.Shared.Next(0, 10));
                    await syncQueue.MarkSyncFailedAsync(items.Select(item => item.SegmentId), exception.Message, next, stoppingToken);
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

    private async Task Delay(CancellationToken token, TimeSpan duration)
    {
        try { await Task.Delay(duration, timeProvider, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
