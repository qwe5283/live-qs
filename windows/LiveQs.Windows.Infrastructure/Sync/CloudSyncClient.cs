using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Uploads activity intervals through the versioned batch contract
/// (POST /api/v1/events/batch) with a Device Token. Each outbox item becomes
/// one contract envelope keyed by a stable event identity; per-item
/// acknowledgements decide whether the outbox entry may be removed.
/// </summary>
public sealed class CloudSyncClient(IHttpClientFactory httpClientFactory, ISyncQueueStore syncQueue, TimeProvider timeProvider) : ISyncClient
{
    /// <summary>The contract limits one batch to 100 items.</summary>
    private const int MaxBatchSize = 100;

    public async Task<IReadOnlyList<SyncOutcome>> UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return [];
        var installId = await syncQueue.GetInstallIdAsync(cancellationToken);
        var outcomes = new List<SyncOutcome>(items.Count);
        foreach (var chunk in items.Chunk(MaxBatchSize))
        {
            outcomes.AddRange(await UploadChunkAsync(chunk, settings, installId, cancellationToken));
        }
        return outcomes;
    }

    private async Task<IReadOnlyList<SyncOutcome>> UploadChunkAsync(
        IReadOnlyList<SyncQueueItem> chunk, AppSettings settings, string installId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("cloud-sync");
        client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);

        var observedAt = timeProvider.GetUtcNow();
        var events = chunk.Select(item => ToEnvelope(item, settings, installId, observedAt)).ToArray();
        using var response = await client.PostAsJsonAsync(
            "api/v1/events/batch", new Core.Contracts.EventBatchRequest { Events = events }, Core.Contracts.ContractJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 300) body = body[..300];
            throw new HttpRequestException($"云端返回 {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }

        var batch = await response.Content.ReadFromJsonAsync<Core.Contracts.EventBatchResponse>(Core.Contracts.ContractJson.Options, cancellationToken);
        if (batch?.Results is null || batch.Results.Length != events.Length)
        {
            throw new InvalidOperationException("云端批量响应数量与请求不一致。");
        }
        return chunk.Zip(batch.Results, MapOutcome).ToArray();
    }

    private static SyncOutcome MapOutcome(SyncQueueItem item, Core.Contracts.EventAcknowledgement acknowledgement) => acknowledgement.Status switch
    {
        // accepted: the revision is stored; duplicate: the exact revision was
        // already stored; stale_revision: a newer revision already won. In all
        // three cases the uploaded revision is acknowledged and the outbox
        // entry may be removed.
        Core.Contracts.Status.Accepted or Core.Contracts.Status.Duplicate or Core.Contracts.Status.StaleRevision
            => new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null),
        _ => new SyncOutcome(item, SyncOutcomeKind.Rejected, DescribeError(acknowledgement)),
    };

    private static string DescribeError(Core.Contracts.EventAcknowledgement acknowledgement) => acknowledgement.Error is { } error
        ? $"{error.Code}: {error.Message}"
        : "rejected";

    internal static Core.Contracts.VersionedEvent ToEnvelope(
        SyncQueueItem item, AppSettings settings, string installId, DateTimeOffset observedAt)
    {
        var payload = new Core.Contracts.Payload
        {
            // AppId is the executable name (never the full path); AppName is
            // local file-description metadata, not raw window text.
            ApplicationId = item.AppId,
            IsAfk = item.IsAfk,
            Duration = new Core.Contracts.Duration
            {
                Value = (long)Math.Round((item.EndedAt - item.StartedAt).TotalMilliseconds),
                Unit = Core.Contracts.DurationUnit.Ms,
            },
        };
        if (!string.IsNullOrWhiteSpace(item.AppName)) payload.ApplicationLabel = item.AppName;

        return new Core.Contracts.VersionedEvent
        {
            EventId = EventIds.ForSegment(settings.DeviceId, installId, item.SegmentId),
            EventType = Core.Contracts.EventType.ActivityInterval,
            SchemaVersion = 1,
            OwnerId = settings.OwnerId,
            Source = new Core.Contracts.Source
            {
                Kind = Core.Contracts.SourceKind.WindowsForeground,
                RecordId = $"segment-{item.SegmentId.ToString(CultureInfo.InvariantCulture)}",
            },
            Device = new Core.Contracts.Device { Id = settings.DeviceId, Platform = Core.Contracts.Platform.Windows },
            StartAt = WireInstant(item.StartedAt),
            EndAt = WireInstant(item.EndedAt),
            CaptureTimezone = LocalIanaName(),
            CaptureOffsetMinutes = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(item.StartedAt).TotalMinutes),
            PrivacyLevel = Core.Contracts.PrivacyLevel.Normal,
            Revision = item.SyncVersion,
            FinalizationState = item.Finalized ? Core.Contracts.FinalizationState.Final : Core.Contracts.FinalizationState.Checkpoint,
            Provenance = new Core.Contracts.Provenance
            {
                CollectorVersion = CollectorVersion(),
                ObservedAt = WireInstant(observedAt),
            },
            Invalidated = false,
            Payload = payload,
        };
    }

    private static string WireInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string LocalIanaName() =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaId) ? ianaId : "UTC";

    private static string CollectorVersion()
    {
        var version = typeof(CloudSyncClient).Assembly.GetName().Version;
        var major = version?.Major ?? 0;
        var minor = version?.Minor ?? 0;
        var build = version is null || version.Build < 0 ? 0 : version.Build;
        return FormattableString.Invariant($"{major}.{minor}.{build}");
    }
}
