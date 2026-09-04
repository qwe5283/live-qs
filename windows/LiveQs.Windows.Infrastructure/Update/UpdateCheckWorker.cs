using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Update;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Update;

/// <summary>
/// Periodically fetches this component's own update manifest, evaluates it
/// against the running version, and for an applicable release downloads and
/// SHA-256-verifies the artifact once. The result is a diagnosable status for
/// the tray and settings; nothing is ever installed automatically — the Owner
/// triggers the installation manually. Every refusal (invalid manifest, other
/// component's manifest, incompatible minimum version, digest mismatch)
/// surfaces with a stable code and never looks like an update.
/// </summary>
public sealed class UpdateCheckWorker(
    ISettingsStore settingsStore,
    IUpdateCheckClient client,
    IUpdateStateStore stateStore,
    IUpdateStatusService statusService,
    IAppVersion appVersion,
    IAppPaths paths,
    TimeProvider timeProvider,
    ILogger<UpdateCheckWorker> logger) : BackgroundService
{
    /// <summary>Update checks are rare by design: at startup, then every six hours.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

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
                logger.LogError(exception, "Update check iteration failed.");
            }

            await Delay(stoppingToken, CheckInterval);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetSettingsAsync(cancellationToken);
        if (!settings.UpdateCheckEnabled || string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
        {
            statusService.Update(new UpdateStatus(UpdateCheckState.Idle, settings.UpdateCheckEnabled));
            return;
        }

        var manifest = await FetchManifestAsync(settings, cancellationToken);
        if (manifest is null) return; // the diagnosable failure status is already published

        var decision = UpdateEvaluator.Evaluate(UpdateComponents.Windows, appVersion.CurrentVersion, manifest);
        if (decision.Kind == UpdateDecisionKind.UpToDate)
        {
            statusService.Update(new UpdateStatus(
                UpdateCheckState.UpToDate,
                settings.UpdateCheckEnabled,
                LastCheckedAt: timeProvider.GetUtcNow(),
                LastErrorCode: UpdateCodes.ManifestVersionNotNewer));
            return;
        }
        if (decision.Kind == UpdateDecisionKind.Refuse)
        {
            var state = decision.Code == UpdateCodes.MinCompatibleNotMet
                ? UpdateCheckState.Incompatible
                : UpdateCheckState.Failed;
            statusService.Update(new UpdateStatus(
                state,
                settings.UpdateCheckEnabled,
                decision.Version,
                decision.ReleasedAt,
                decision.DownloadUrl,
                LastCheckedAt: timeProvider.GetUtcNow(),
                LastErrorCode: decision.Code,
                LastErrorMessage: decision.Detail ?? ""));
            return;
        }

        await VerifyAndKeepPackageAsync(settings, manifest, cancellationToken);
    }

    private async Task<UpdateManifest?> FetchManifestAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        string manifestText;
        try
        {
            manifestText = await client.FetchManifestAsync(settings.UpdateManifestUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown is not a diagnosable update failure
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            statusService.Update(new UpdateStatus(
                UpdateCheckState.Failed,
                settings.UpdateCheckEnabled,
                LastCheckedAt: timeProvider.GetUtcNow(),
                LastErrorCode: UpdateCodes.ManifestFetchFailed,
                LastErrorMessage: exception.Message));
            return null;
        }

        if (UpdateManifestParser.TryParse(manifestText, out var manifest, out var errors)) return manifest;

        statusService.Update(new UpdateStatus(
            UpdateCheckState.Failed,
            settings.UpdateCheckEnabled,
            LastCheckedAt: timeProvider.GetUtcNow(),
            LastErrorCode: UpdateCodes.ManifestParseFailed,
            LastErrorMessage: string.Join("; ", errors)));
        return null;
    }

    private async Task VerifyAndKeepPackageAsync(AppSettings settings, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        var verified = await stateStore.GetAsync(cancellationToken);
        if (verified.Version == manifest.Version &&
            verified.Sha256 == manifest.Sha256 &&
            verified.PackagePath is { } existing && File.Exists(existing))
        {
            PublishAvailable(settings, manifest, existing);
            return;
        }

        try
        {
            var result = await client.DownloadArtifactAsync(
                manifest.DownloadUrl,
                Path.Combine(paths.DataDirectory, "updates"),
                manifest.Sha256,
                cancellationToken);
            await stateStore.SaveAsync(new VerifiedPackage(manifest.Version, result.PackagePath, result.Sha256), cancellationToken);
            PublishAvailable(settings, manifest, result.PackagePath);
        }
        catch (UpdateCheckException exception)
        {
            logger.LogWarning("The update artifact was refused: {Code}", exception.Code);
            statusService.Update(new UpdateStatus(
                UpdateCheckState.Failed,
                settings.UpdateCheckEnabled,
                manifest.Version,
                manifest.ReleasedAt,
                manifest.DownloadUrl,
                LastCheckedAt: timeProvider.GetUtcNow(),
                LastErrorCode: exception.Code,
                LastErrorMessage: exception.Message));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException
            && !cancellationToken.IsCancellationRequested)
        {
            statusService.Update(new UpdateStatus(
                UpdateCheckState.Failed,
                settings.UpdateCheckEnabled,
                manifest.Version,
                manifest.ReleasedAt,
                manifest.DownloadUrl,
                LastCheckedAt: timeProvider.GetUtcNow(),
                LastErrorCode: UpdateCodes.ArtifactDownloadFailed,
                LastErrorMessage: exception.Message));
        }
    }

    private void PublishAvailable(AppSettings settings, UpdateManifest manifest, string packagePath) =>
        statusService.Update(new UpdateStatus(
            UpdateCheckState.Available,
            settings.UpdateCheckEnabled,
            manifest.Version,
            manifest.ReleasedAt,
            manifest.DownloadUrl,
            packagePath,
            timeProvider.GetUtcNow(),
            UpdateCodes.UpdateAvailable));

    private async Task Delay(CancellationToken token, TimeSpan duration)
    {
        try { await Task.Delay(duration, timeProvider, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
