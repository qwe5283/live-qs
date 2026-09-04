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
    /// permanent rejections never retry; transport failures back off. Every
    /// enabled pass ends with a diagnostics push so the Owner sees the queue
    /// drain step by step after an outage.
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
            await PushDiagnosticsAsync(settings, cancellationToken);
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
            // One ring-buffer entry per distinct stable code per pass keeps a
            // large rejected batch from drowning the recent-error history.
            foreach (var group in rejections.GroupBy(rejection => rejection.ErrorCode ?? "rejected"))
            {
                await syncQueue.RecordSyncErrorAsync(
                    group.Key, SafeRejectionMessage(group.First()), timeProvider.GetUtcNow(), cancellationToken);
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
            await PushDiagnosticsAsync(settings, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var attempt = items.Max(item => item.AttemptCount) + 1;
            var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(attempt, 8)));
            var next = timeProvider.GetUtcNow().AddSeconds(seconds + Random.Shared.Next(0, 10));
            await syncQueue.MarkSyncFailedAsync(items.Select(item => item.SegmentId), exception.Message, next, cancellationToken);
            logger.LogWarning(exception, "Cloud sync failed; local collection remains active.");
            // The ring buffer gets a stable code and a safe summary only: the
            // raw exception text can embed local content and never leaves the
            // device (it stays in the local log and tray state).
            var (code, message) = SyncErrorDescriber.Describe(exception);
            await syncQueue.RecordSyncErrorAsync(code, message, timeProvider.GetUtcNow(), cancellationToken);
            _lastError = exception.Message;
            statusService.Update(new SyncStatus(true, false, pending, _lastSuccess, _lastError));
            await PushDiagnosticsAsync(settings, cancellationToken);
        }
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
        if (string.IsNullOrWhiteSpace(settings.DeviceToken)) return;
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

    /// <summary>The server's safe contract message without the "code: " prefix the tray view adds.</summary>
    private static string SafeRejectionMessage(SyncOutcome rejection)
    {
        var error = rejection.Error ?? "The event was rejected.";
        if (rejection.ErrorCode is { } code && error.StartsWith($"{code}: ", StringComparison.Ordinal))
        {
            return error[(code.Length + 2)..];
        }
        return error;
    }

    private async Task Delay(CancellationToken token, TimeSpan duration)
    {
        try { await Task.Delay(duration, timeProvider, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
