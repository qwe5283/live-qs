using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Classification;
using LiveQs.Windows.Core.Reclassification;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Executes explicit historical reclassification tasks the Owner started.
/// Polling is bounded and idle-polling is cheap: when an assignment arrives,
/// the worker refreshes its cached rule set to the task's target version,
/// re-runs the local engine over locally retained finalized activity, submits
/// higher revisions for changed interpretations through the batch protocol,
/// and reports the outcome counts. The server never reclassifies by itself —
/// only this device still holds the raw context — and events whose context
/// has aged out of local retention are simply never claimed, which the server
/// reports as unrecoverable instead of silently skipping them.
/// </summary>
public sealed class ReclassificationWorker(
    ISettingsStore settingsStore,
    ISyncQueueStore syncQueue,
    IReclassificationClient reclassificationClient,
    ISyncClient syncClient,
    IClassificationRuleSync classificationRuleSync,
    IClassificationRuleStore classificationRuleStore,
    TimeProvider timeProvider,
    ILogger<ReclassificationWorker> logger) : BackgroundService
{
    /// <summary>How often the worker polls for an open task; an idle poll is a single small GET.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Candidates are processed in pages so a large history cannot pin memory.</summary>
    internal const int PageSize = 200;

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
                logger.LogError(exception, "Reclassification pass failed; local collection remains active.");
            }

            try { await Task.Delay(PollInterval, timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    /// One assignment-poll-and-execute pass. Cloud sync must be enabled: a
    /// reclassification is worthless if its higher revisions cannot upload.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetSettingsAsync(cancellationToken);
        if (!settings.CloudSyncEnabled) return;

        var assignment = await reclassificationClient.GetAssignmentAsync(settings, cancellationToken);
        if (assignment is null) return;

        var ruleSet = await EnsureTargetRuleSetAsync(assignment, settings, cancellationToken);
        if (ruleSet is null)
        {
            logger.LogWarning(
                "Reclassification task {TaskId} deferred: the target rule set version {TargetVersion} is not available yet.",
                assignment.TaskId, assignment.TargetRuleSetVersion);
            return;
        }

        var classificationSecret = await classificationRuleStore.GetClassificationSecretAsync(cancellationToken);
        var report = await ProcessAsync(assignment, ruleSet, classificationSecret, settings, cancellationToken);
        await reclassificationClient.ReportAsync(assignment.TaskId, report, settings, cancellationToken);
        logger.LogInformation(
            "Reclassification pass for task {TaskId} reported: scanned {Scanned}, reclassified {Reclassified}, unchanged {Unchanged}, failed {Failed}.",
            assignment.TaskId, report.Scanned, report.Reclassified, report.Unchanged, report.Failed);
    }

    /// <summary>
    /// The task's target rule set version is the minimum the pass may run
    /// under: re-evaluating with an older rule set would submit revisions the
    /// Owner never asked for. A forced refresh happens when the cache is
    /// behind; a failed refresh defers the task to the next poll instead.
    /// </summary>
    private async Task<Core.Contracts.ClassificationRuleSet?> EnsureTargetRuleSetAsync(
        ReclassificationAssignment assignment, AppSettings settings, CancellationToken cancellationToken)
    {
        var cached = await classificationRuleStore.GetCachedRuleSetAsync(cancellationToken);
        var force = cached is null || cached.RuleSetVersion < assignment.TargetRuleSetVersion;
        var ruleSet = await classificationRuleSync.RefreshAsync(settings, cancellationToken, forceRefresh: force);
        return ruleSet is not null && ruleSet.RuleSetVersion >= assignment.TargetRuleSetVersion ? ruleSet : null;
    }

    private async Task<ReclassificationReport> ProcessAsync(
        ReclassificationAssignment assignment,
        Core.Contracts.ClassificationRuleSet ruleSet,
        string classificationSecret,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        int scanned = 0, reclassified = 0, unchanged = 0, failed = 0;
        long after = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await syncQueue.GetReclassificationCandidatesAsync(
                assignment.From, assignment.To, PageSize, after, cancellationToken);
            if (candidates.Count == 0) break;
            after = candidates.Max(item => item.SegmentId);

            var decisions = new List<ReclassificationDecision>(candidates.Count);
            foreach (var segment in candidates)
            {
                scanned += 1;
                // AFK segments never reach this query, so the engine always
                // has an activity to name or deliberately declines to.
                var recomputed = ClassificationEngine.Classify(
                    ruleSet, "windows", segment.AppId, segment.WindowTitle, classificationSecret);
                var decision = ReclassificationPlanner.Decide(segment, recomputed);
                if (decision.Action == ReclassificationAction.SubmitRevision) decisions.Add(decision);
                else unchanged += 1;
            }

            if (decisions.Count > 0)
            {
                var outcomes = await syncClient.UploadReclassificationAsync(decisions, settings, cancellationToken);
                foreach (var outcome in outcomes)
                {
                    switch (outcome.Kind)
                    {
                        case SyncOutcomeKind.Acknowledged when outcome.Status == Core.Contracts.EventAcknowledgementStatus.StaleRevision:
                            // A manual Owner correction or a newer revision
                            // protects this event: the device yields and the
                            // event counts as needing no change.
                            unchanged += 1;
                            break;
                        case SyncOutcomeKind.Acknowledged:
                            reclassified += 1;
                            break;
                        case SyncOutcomeKind.Rejected:
                            failed += 1;
                            break;
                    }
                }
            }
        }
        return new ReclassificationReport(scanned, reclassified, unchanged, failed);
    }
}
