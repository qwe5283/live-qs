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
    IClassificationRuleSync classificationRuleSync,
    TimeProvider timeProvider,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private DateTimeOffset? _lastSuccess;
    private string _lastError = "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Sync worker iteration failed.");
            }

            await Delay(stoppingToken, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// One poll-and-upload pass. The outbox entry is removed only once its
    /// revision is acknowledged (accepted, duplicate, or stale-revision);
    /// permanent rejections never retry; transport failures back off.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetSettingsAsync(cancellationToken);
        var pending = await syncQueue.GetPendingSyncCountAsync(cancellationToken);
        if (!settings.CloudSyncEnabled)
        {
            statusService.Update(new SyncStatus(false, false, pending, _lastSuccess, _lastError));
            return;
        }

        var items = await syncQueue.GetPendingSyncAsync(100, timeProvider.GetUtcNow(), cancellationToken);
        if (items.Count == 0)
        {
            statusService.Update(new SyncStatus(true, false, pending, _lastSuccess, _lastError));
            return;
        }

        statusService.Update(new SyncStatus(true, true, pending, _lastSuccess, _lastError));
        try
        {
            // Keep the cached rule set near the Owner's published version; a
            // failed refresh is non-fatal and classification continues with
            // the last successful version (also fully offline).
            await classificationRuleSync.RefreshAsync(settings, cancellationToken);
            var outcomes = await client.UploadAsync(items, settings, cancellationToken);
            var acknowledged = outcomes
                .Where(outcome => outcome.Kind == SyncOutcomeKind.Acknowledged)
                .Select(outcome => outcome.Item.SegmentId)
                .ToArray();
            var rejections = outcomes
                .Where(outcome => outcome.Kind == SyncOutcomeKind.Rejected)
                .ToArray();

            foreach (var rejection in rejections)
            {
                await syncQueue.MarkPermanentAsync(
                    [rejection.Item.SegmentId], rejection.Error ?? "rejected", timeProvider.GetUtcNow(), cancellationToken);
            }
            if (acknowledged.Length > 0)
            {
                _lastSuccess = timeProvider.GetUtcNow();
                _lastError = "";
                await syncQueue.MarkSyncedAsync(acknowledged, _lastSuccess.Value, cancellationToken);
            }
            if (rejections.Length > 0)
            {
                _lastError = $"永久拒绝：{rejections[0].Error}";
            }

            pending = await syncQueue.GetPendingSyncCountAsync(cancellationToken);
            statusService.Update(new SyncStatus(true, false, pending, _lastSuccess, _lastError));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var attempt = items.Max(item => item.AttemptCount) + 1;
            var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(attempt, 8)));
            var next = timeProvider.GetUtcNow().AddSeconds(seconds + Random.Shared.Next(0, 10));
            await syncQueue.MarkSyncFailedAsync(items.Select(item => item.SegmentId), exception.Message, next, cancellationToken);
            logger.LogWarning(exception, "Cloud sync failed; local collection remains active.");
            _lastError = exception.Message;
            statusService.Update(new SyncStatus(true, false, pending, _lastSuccess, _lastError));
        }
    }

    private async Task Delay(CancellationToken token, TimeSpan duration)
    {
        try { await Task.Delay(duration, timeProvider, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
