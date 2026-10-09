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
    IDiagnosticsClient diagnosticsClient,
    TimeProvider timeProvider,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);
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

            await DelayAsync(stoppingToken, _pollInterval);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetSettingsAsync(cancellationToken);
        var pending = await syncQueue.GetPendingSyncCountAsync(cancellationToken);
        _lastSuccess ??= (await syncQueue.GetSyncOverviewAsync(cancellationToken)).LastSuccessfulUploadAt;
        if (!settings.CloudSyncEnabled)
        {
            statusService.Update(new SyncStatus(false, false, pending, _lastSuccess, _lastError));
            return;
        }

        var items = await syncQueue.GetPendingSyncAsync(MaxBatchSize, timeProvider.GetUtcNow(), cancellationToken);
        if (items.Count > 0)
        {
            statusService.Update(new SyncStatus(true, true, pending, _lastSuccess, _lastError));
            await UploadPendingAsync(settings, items, cancellationToken);
        }

        pending = await syncQueue.GetPendingSyncCountAsync(cancellationToken);
        statusService.Update(new SyncStatus(true, false, pending, _lastSuccess, _lastError));
        await PushDiagnosticsAsync(settings, cancellationToken);
    }

    private async Task UploadPendingAsync(
        AppSettings settings,
        IReadOnlyList<SyncQueueItem> items,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["BatchId"] = Guid.NewGuid().ToString("N"),
            ["DeviceId"] = settings.DeviceId,
        });
        try
        {
            // A failed rule refresh keeps the cache usable and must not prevent uploads.
            await classificationRuleSync.RefreshAsync(settings, cancellationToken);
            var outcomes = await client.UploadAsync(items, settings, cancellationToken);
            await ApplyOutcomesAsync(outcomes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            await ScheduleRetryAsync(items, exception, cancellationToken);
        }
    }

    private async Task ApplyOutcomesAsync(IReadOnlyList<SyncOutcome> outcomes, CancellationToken cancellationToken)
    {
        var acknowledged = outcomes
            .Where(outcome => outcome.Kind == SyncOutcomeKind.Acknowledged)
            .Select(outcome => outcome.Item.Revision)
            .ToArray();
        var rejections = outcomes.Where(outcome => outcome.Kind == SyncOutcomeKind.Rejected).ToArray();
        foreach (var rejection in rejections)
        {
            await syncQueue.MarkPermanentAsync(
                [rejection.Item.Revision], rejection.DisplayError, timeProvider.GetUtcNow(), cancellationToken);
            logger.LogWarning(
                "Upload rejected segment {SegmentId} revision {Revision}: {Code}.",
                rejection.Item.SegmentId, rejection.Item.SyncVersion, rejection.ErrorCode ?? "rejected");
        }

        // One entry per code prevents a large rejected batch from exhausting the diagnostics ring.
        foreach (var group in rejections.GroupBy(rejection => rejection.ErrorCode ?? "rejected"))
        {
            await syncQueue.RecordSyncErrorAsync(
                group.Key, group.First().ErrorMessage ?? "The event was rejected.", timeProvider.GetUtcNow(), cancellationToken);
        }
        if (acknowledged.Length > 0)
        {
            _lastSuccess = timeProvider.GetUtcNow();
            _lastError = "";
            await syncQueue.MarkSyncedAsync(acknowledged, _lastSuccess.Value, cancellationToken);
        }
        if (rejections.Length > 0)
        {
            _lastError = $"永久拒绝：{rejections[0].DisplayError}";
        }
    }

    private async Task ScheduleRetryAsync(
        IReadOnlyList<SyncQueueItem> items,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var attempt = items.Max(item => item.AttemptCount) + 1;
        var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(attempt, 8)));
        var nextAttemptAt = timeProvider.GetUtcNow().AddSeconds(seconds + Random.Shared.Next(0, 10));
        await syncQueue.MarkSyncFailedAsync(
            items.Select(item => item.SegmentId), exception.Message, nextAttemptAt, cancellationToken);
        logger.LogWarning(exception, "Cloud sync failed; local collection remains active.");

        // Raw exception text may contain local data; diagnostics upload only a fixed safe summary.
        var (code, message) = SyncErrorDescriber.Describe(exception);
        await syncQueue.RecordSyncErrorAsync(code, message, timeProvider.GetUtcNow(), cancellationToken);
        _lastError = exception.Message;
    }

    /// <summary>
    /// Builds the snapshot from local storage (not from memory) and pushes it.
    /// Reading from storage makes the snapshot identical across process
    /// restarts: last success, queue depth, and the recent-error history all
    /// survive, so unacknowledged outbox and failure-queue state stay
    /// consistent. A failed push never breaks the sync loop.
    /// </summary>
    private async Task PushDiagnosticsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.DeviceToken))
        {
            return;
        }

        try
        {
            var overview = await syncQueue.GetSyncOverviewAsync(cancellationToken);
            var recentErrors = await syncQueue.GetRecentSyncErrorsAsync(10, cancellationToken);
            await diagnosticsClient.PushAsync(
                new SyncDiagnosticsSnapshot(
                    overview.LastCollectionAt,
                    overview.LastSuccessfulUploadAt,
                    overview.OldestPendingAt,
                    overview.PendingCount,
                    overview.PermanentFailureCount,
                    recentErrors),
                settings,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Sync diagnostics push failed; the next pass will retry.");
        }
    }

    private async Task DelayAsync(CancellationToken token, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, timeProvider, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
