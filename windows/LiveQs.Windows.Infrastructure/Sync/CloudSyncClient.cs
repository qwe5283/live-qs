using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Uploads activity intervals through the versioned batch contract
/// (POST /api/v1/events/batch) with a Device Token. Each outbox item becomes
/// one contract envelope keyed by a stable event identity; per-item
/// acknowledgements decide whether the outbox entry may be removed.
/// Classification runs locally against the cached Owner rule set: the upload
/// carries only the subject, rule id, rule version, and confidence — never
/// the raw window title it was derived from. Acknowledged uploads record the
/// accepted outcome so explicit reclassification passes can detect unchanged
/// events instead of burning no-op revisions.
/// </summary>
public sealed class CloudSyncClient(
    IHttpClientFactory httpClientFactory,
    ISyncQueueStore syncQueue,
    IClassificationRuleStore classificationRuleStore,
    TimeProvider timeProvider) : ISyncClient
{
    /// <summary>The contract limits one batch to 100 items.</summary>
    private const int MaxBatchSize = 100;

    public async Task<IReadOnlyList<SyncOutcome>> UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return [];
        var installId = await syncQueue.GetInstallIdAsync(cancellationToken);
        var cachedRuleSet = await classificationRuleStore.GetCachedRuleSetAsync(cancellationToken);
        var classificationSecret = await classificationRuleStore.GetClassificationSecretAsync(cancellationToken);
        var outcomes = new List<SyncOutcome>(items.Count);
        foreach (var chunk in items.Chunk(MaxBatchSize))
        {
            var entries = chunk
                .Select(item => (Item: item, Outcome: ClassifyItem(item, cachedRuleSet, classificationSecret)))
                .ToArray();
            outcomes.AddRange(await UploadChunkAsync(entries, settings, installId, cancellationToken));
        }
        return outcomes;
    }

    /// <summary>
    /// Uploads pre-computed reclassification decisions (same event identity,
    /// bumped revision, re-interpreted classification) and records the local
    /// consequence of each acknowledgement on the segment.
    /// </summary>
    public async Task<IReadOnlyList<SyncOutcome>> UploadReclassificationAsync(
        IReadOnlyList<ReclassificationDecision> decisions,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (decisions.Count == 0) return [];
        var installId = await syncQueue.GetInstallIdAsync(cancellationToken);
        var outcomes = new List<SyncOutcome>(decisions.Count);
        foreach (var chunk in decisions.Chunk(MaxBatchSize))
        {
            var uploaded = await UploadChunkAsync(
                chunk.Select(decision => (decision.Segment, decision.Outcome)).ToArray(),
                settings, installId, cancellationToken);
            for (var index = 0; index < uploaded.Count; index++)
            {
                var outcome = uploaded[index];
                var decision = chunk[index];
                outcomes.Add(outcome);
                if (outcome.Kind != SyncOutcomeKind.Acknowledged) continue;
                if (outcome.Status == Core.Contracts.EventAcknowledgementStatus.StaleRevision)
                {
                    // A manual Owner correction (or any newer revision) wins:
                    // the device yields, keeping its local revision so no
                    // later upload can ever fight the human interpretation,
                    // and records the attempted interpretation so the next
                    // pass treats this segment as settled.
                    await syncQueue.RecordUploadOutcomeAsync(decision.Segment.SegmentId, decision.Outcome, cancellationToken);
                }
                else
                {
                    await syncQueue.RecordReclassifiedAsync(
                        decision.Segment.SegmentId, decision.Segment.SyncVersion, decision.Outcome, cancellationToken);
                }
            }
        }
        return outcomes;
    }

    private async Task<IReadOnlyList<SyncOutcome>> UploadChunkAsync(
        IReadOnlyList<(SyncQueueItem Item, ClassificationOutcome? Outcome)> entries,
        AppSettings settings,
        string installId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("cloud-sync");
        client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);

        var observedAt = timeProvider.GetUtcNow();
        var events = entries.Select(entry => ToEnvelope(
            entry.Item, settings, installId, observedAt, entry.Outcome)).ToArray();
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
        var outcomes = entries
            .Zip(batch.Results, (entry, acknowledgement) => MapOutcome(entry.Item, acknowledgement))
            .ToArray();
        for (var index = 0; index < outcomes.Length; index++)
        {
            if (outcomes[index].Kind != SyncOutcomeKind.Acknowledged) continue;
            var (item, computed) = entries[index];
            await syncQueue.RecordUploadOutcomeAsync(item.SegmentId, computed, cancellationToken);
        }
        return outcomes;
    }

    /// <summary>
    /// Classifies a pending item against the cached rule set. AFK intervals
    /// carry no subject: there is no activity to name while the user is away.
    /// </summary>
    internal static ClassificationOutcome? ClassifyItem(
        SyncQueueItem item,
        Core.Contracts.ClassificationRuleSet? cachedRuleSet,
        string classificationSecret)
    {
        if (item.IsAfk) return null;
        return ClassificationEngine.Classify(
            cachedRuleSet, "windows", item.AppId, item.WindowTitle, classificationSecret);
    }

    private static SyncOutcome MapOutcome(SyncQueueItem item, Core.Contracts.EventAcknowledgement acknowledgement) => acknowledgement.Status switch
    {
        // accepted: the revision is stored; duplicate: the exact revision was
        // already stored; stale_revision: a newer revision already won. In all
        // three cases the uploaded revision is acknowledged and the outbox
        // entry may be removed.
        Core.Contracts.EventAcknowledgementStatus.Accepted or Core.Contracts.EventAcknowledgementStatus.Duplicate or Core.Contracts.EventAcknowledgementStatus.StaleRevision
            => new SyncOutcome(item, SyncOutcomeKind.Acknowledged, null, acknowledgement.Status),
        _ => new SyncOutcome(item, SyncOutcomeKind.Rejected, DescribeError(acknowledgement), acknowledgement.Status),
    };

    private static string DescribeError(Core.Contracts.EventAcknowledgement acknowledgement) => acknowledgement.Error is { } error
        ? $"{error.Code}: {error.Message}"
        : "rejected";

    internal static Core.Contracts.VersionedEvent ToEnvelope(
        SyncQueueItem item,
        AppSettings settings,
        string installId,
        DateTimeOffset observedAt,
        ClassificationOutcome? classification = null)
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
        if (classification is { } outcome)
        {
            // Explainable labels only: subject, rule id, rule version,
            // confidence. The matched title stays local.
            payload.SubjectId = outcome.SubjectId;
            payload.Classification = new Core.Contracts.Classification
            {
                RuleId = outcome.RuleId,
                RuleVersion = outcome.RuleVersion,
                Confidence = outcome.Confidence,
            };
        }

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
