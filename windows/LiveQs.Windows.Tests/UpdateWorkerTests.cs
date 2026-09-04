using System.Net;
using System.Text;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Update;
using LiveQs.Windows.Infrastructure.Configuration;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveQs.Windows.Tests;

/// <summary>Scripted IUpdateCheckClient: canned manifest text and download behavior, with call counters.</summary>
internal sealed class StubUpdateCheckClient : IUpdateCheckClient
{
    public Func<string> ManifestFactory { get; set; } = () => UpdateManifestText("windows", "0.2.0");
    public Exception? FetchFailure { get; set; }
    public Exception? DownloadFailure { get; set; }
    public Func<string, UpdateDownloadResult>? DownloadHandler { get; set; }
    public int FetchCount { get; private set; }
    public int DownloadCount { get; private set; }

    public Task<string> FetchManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        FetchCount++;
        if (FetchFailure is not null) throw FetchFailure;
        return Task.FromResult(ManifestFactory());
    }

    public Task<UpdateDownloadResult> DownloadArtifactAsync(
        string downloadUrl, string destinationDirectory, string expectedSha256, CancellationToken cancellationToken = default)
    {
        DownloadCount++;
        if (DownloadFailure is not null) throw DownloadFailure;
        if (DownloadHandler is { } handler) return Task.FromResult(handler(destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        var packagePath = Path.Combine(destinationDirectory, "LiveQs.Windows-0.2.0-win-x64.zip");
        File.WriteAllText(packagePath, "package");
        string sha256;
        using (var stream = File.OpenRead(packagePath)) sha256 = UpdateArtifactHash.Compute(stream);
        return Task.FromResult(new UpdateDownloadResult(packagePath, sha256));
    }

    public static string UpdateManifestText(string component, string version, string? minCompatible = null) => $$"""
        {
          "manifest_version": 1,
          "component": "{{component}}",
          "version": "{{version}}",
          "released_at": "2026-09-04T08:00:00Z",
          "download_url": "https://github.com/qwe5283/live-qs/releases/download/{{component}}%2Fv{{version}}/artifact",
          "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
          "min_compatible_version": "{{minCompatible ?? "0.1.0"}}"
        }
        """;
}

public sealed class UpdateWorkerTests : IDisposable
{
    private readonly UpdateTestPaths _paths = new();
    private readonly DateTimeOffset _now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly StubUpdateCheckClient _client = new();
    private readonly UpdateStatusService _statusService = new();
    private readonly UpdateStateStore _stateStore;

    public UpdateWorkerTests()
    {
        _stateStore = new UpdateStateStore(_paths);
    }

    [Fact]
    public async Task Disabled_ShowsIdleAndNeverTouchesTheNetwork()
    {
        var worker = await CreateWorkerAsync(settings => settings with { UpdateCheckEnabled = false });

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _client.FetchCount);
        Assert.Equal(UpdateCheckState.Idle, _statusService.Current.State);
        Assert.False(_statusService.Current.Enabled);
    }

    [Fact]
    public async Task AvailableUpdate_IsDownloadedVerifiedAndRemembered()
    {
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        var status = _statusService.Current;
        Assert.Equal(UpdateCheckState.Available, status.State);
        Assert.Equal("0.2.0", status.AvailableVersion);
        Assert.Equal(UpdateCodes.UpdateAvailable, status.LastErrorCode);
        Assert.NotNull(status.VerifiedPackagePath);
        Assert.True(File.Exists(status.VerifiedPackagePath), "the verified package must be kept for manual installation");
        Assert.Equal(_now, status.LastCheckedAt);
        var verified = await _stateStore.GetAsync();
        Assert.Equal("0.2.0", verified.Version);
        Assert.Equal(status.VerifiedPackagePath, verified.PackagePath);
    }

    [Fact]
    public async Task Restart_DoesNotRedownloadAnAlreadyVerifiedVersion()
    {
        var existing = Path.Combine(_paths.DataDirectory, "updates", "LiveQs.Windows-0.2.0-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "package");
        await _stateStore.SaveAsync(new VerifiedPackage(
            "0.2.0",
            existing,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _client.FetchCount);
        Assert.Equal(0, _client.DownloadCount);
        Assert.Equal(UpdateCheckState.Available, _statusService.Current.State);
        Assert.Equal(existing, _statusService.Current.VerifiedPackagePath);
    }

    [Fact]
    public async Task ArtifactHashMismatch_RefusesTheUpdateWithADiagnosableState()
    {
        _client.DownloadFailure = new UpdateCheckException(UpdateCodes.ArtifactHashMismatch, "digest differs");
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        var status = _statusService.Current;
        Assert.Equal(UpdateCheckState.Failed, status.State);
        Assert.Equal(UpdateCodes.ArtifactHashMismatch, status.LastErrorCode);
        Assert.Equal("0.2.0", status.AvailableVersion);
        Assert.Null(status.VerifiedPackagePath);
        var verified = await _stateStore.GetAsync();
        Assert.Null(verified.Version);
    }

    [Fact]
    public async Task DownloadTransportFailure_IsDiagnosableAndRetriedNextCadence()
    {
        _client.DownloadFailure = new HttpRequestException("connection reset");
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckState.Failed, _statusService.Current.State);
        Assert.Equal(UpdateCodes.ArtifactDownloadFailed, _statusService.Current.LastErrorCode);
        Assert.Equal("0.2.0", _statusService.Current.AvailableVersion);
    }

    [Fact]
    public async Task ManifestFetchFailure_IsDiagnosable()
    {
        _client.FetchFailure = new HttpRequestException("DNS failure");
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckState.Failed, _statusService.Current.State);
        Assert.Equal(UpdateCodes.ManifestFetchFailed, _statusService.Current.LastErrorCode);
    }

    [Fact]
    public async Task ShutdownCancellation_PropagatesInsteadOfReportingAFailure()
    {
        // A cancelled check at shutdown is not a diagnosable update failure.
        _client.FetchFailure = new OperationCanceledException();
        var worker = await CreateWorkerAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.RunOnceAsync(new CancellationToken(canceled: true)));
        Assert.Equal(UpdateCheckState.Idle, _statusService.Current.State);
    }

    [Fact]
    public async Task InvalidManifest_IsRefusedWithoutDownloadingAnything()
    {
        _client.ManifestFactory = () => "not json at all";
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckState.Failed, _statusService.Current.State);
        Assert.Equal(UpdateCodes.ManifestParseFailed, _statusService.Current.LastErrorCode);
        Assert.Equal(0, _client.DownloadCount);
    }

    [Fact]
    public async Task AnotherComponentsManifest_IsNeverAnUpdateForThisClient()
    {
        // Isolation property: publishing android/v9.0.0 must never make the
        // Windows client report an update.
        _client.ManifestFactory = () => StubUpdateCheckClient.UpdateManifestText("android", "9.0.0");
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckState.Failed, _statusService.Current.State);
        Assert.Equal(UpdateCodes.ManifestComponentMismatch, _statusService.Current.LastErrorCode);
        Assert.Equal(0, _client.DownloadCount);
    }

    [Fact]
    public async Task ClientOlderThanMinCompatible_IsRefusedWithAnIncompatibleState()
    {
        _client.ManifestFactory = () => StubUpdateCheckClient.UpdateManifestText("windows", "9.0.0", minCompatible: "1.0.0");
        var worker = await CreateWorkerAsync(currentVersion: "0.0.9");

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckState.Incompatible, _statusService.Current.State);
        Assert.Equal(UpdateCodes.MinCompatibleNotMet, _statusService.Current.LastErrorCode);
        Assert.Equal(0, _client.DownloadCount);
    }

    [Fact]
    public async Task SameVersion_ShowsUpToDateWithoutDownloading()
    {
        _client.ManifestFactory = () => StubUpdateCheckClient.UpdateManifestText("windows", "0.1.0");
        var worker = await CreateWorkerAsync();

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckState.UpToDate, _statusService.Current.State);
        Assert.Equal(UpdateCodes.ManifestVersionNotNewer, _statusService.Current.LastErrorCode);
        Assert.Equal(0, _client.DownloadCount);
        Assert.Equal(_now, _statusService.Current.LastCheckedAt);
    }

    [Fact]
    public async Task Client_FetchManifest_PerformsGetOnTheConfiguredUrl()
    {
        var handler = new RecordingHandler { ResponseFactory = _ => StubUpdateCheckClient.UpdateManifestText("windows", "0.2.0") };
        var client = new UpdateCheckClient(new SingleClientFactory(handler));

        var manifest = await client.FetchManifestAsync("https://example.com/liveqs-windows-update.json");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("https://example.com/liveqs-windows-update.json", handler.Request.RequestUri!.AbsoluteUri);
        Assert.Contains("\"component\": \"windows\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_DownloadArtifact_StreamsVerifiesAndKeepsTheFile()
    {
        var handler = new RecordingHandler { ResponseFactory = _ => "" };
        var client = new UpdateCheckClient(new SingleClientFactory(handler));
        var destination = Path.Combine(_paths.DataDirectory, "updates");

        var result = await client.DownloadArtifactAsync(
            "https://example.com/LiveQs.Windows-0.2.0-win-x64.zip",
            destination,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        Assert.True(File.Exists(result.PackagePath));
        Assert.EndsWith("LiveQs.Windows-0.2.0-win-x64.zip", result.PackagePath, StringComparison.Ordinal);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Sha256);
    }

    [Fact]
    public async Task Client_DownloadArtifact_RefusesAMismatchedDigestAndLeavesNoFile()
    {
        var handler = new RecordingHandler { ResponseFactory = _ => "tampered" };
        var client = new UpdateCheckClient(new SingleClientFactory(handler));
        var destination = Path.Combine(_paths.DataDirectory, "updates");

        var failure = await Assert.ThrowsAsync<UpdateCheckException>(() => client.DownloadArtifactAsync(
            "https://example.com/LiveQs.Windows-0.2.0-win-x64.zip",
            destination,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

        Assert.Equal(UpdateCodes.ArtifactHashMismatch, failure.Code);
        Assert.Empty(Directory.GetFiles(destination));
    }

    private async Task<UpdateCheckWorker> CreateWorkerAsync(
        Func<AppSettings, AppSettings>? configure = null,
        string currentVersion = "0.1.0")
    {
        var repository = new SqliteActivityRepository(_paths, TimeProvider.System);
        await repository.InitializeAsync();
        var settings = (await repository.GetSettingsAsync()) with
        {
            UpdateManifestUrl = "https://example.com/liveqs-windows-update.json",
        };
        if (configure is not null) settings = configure(settings);
        await repository.SaveSettingsAsync(settings);
        return new UpdateCheckWorker(
            repository,
            _client,
            _stateStore,
            _statusService,
            new StubAppVersion(currentVersion),
            _paths,
            new FixedTimeProvider(_now),
            NullLogger<UpdateCheckWorker>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory)) Directory.Delete(_paths.DataDirectory, true);
    }
}

internal sealed class StubAppVersion(string version) : IAppVersion
{
    public string CurrentVersion { get; } = version;
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class UpdateTestPaths : IAppPaths
{
    public UpdateTestPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "LiveQs.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "test.db");
    public string LogPath => Path.Combine(DataDirectory, "test.log");
}
