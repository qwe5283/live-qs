using System.Net.Http.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using LiveQs.Windows.Infrastructure.Http;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Pushes the device sync-state snapshot through the diagnostics contract
/// (POST /api/v1/diagnostics/sync) with a Device Token. Snapshots carry
/// counts, timestamps, and stable-code errors only — the summary strings are
/// the safe ones fixed by <see cref="SyncErrorDescriber"/>, never exception
/// text, raw titles, or tokens.
/// </summary>
public sealed class DiagnosticsClient(IHttpClientFactory httpClientFactory) : IDiagnosticsClient
{
    public async Task PushAsync(
        SyncDiagnosticsSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var client = CloudApiClient.Create(httpClientFactory, settings);

        var report = new Core.Contracts.SyncDiagnosticsReport
        {
            Platform = Core.Contracts.Platform.Windows,
            DeviceName = settings.DeviceId,
            CollectedAt = ContractTime.Format(snapshot.CollectedAt),
            LastSuccessfulUploadAt = ContractTime.Format(snapshot.LastSuccessfulUploadAt),
            OldestPendingAt = ContractTime.Format(snapshot.OldestPendingAt),
            PendingCount = snapshot.PendingCount,
            PermanentFailureCount = snapshot.PermanentFailureCount,
            RecentErrors = snapshot.RecentErrors.Select(ToContract).ToArray(),
        };
        using var response = await client.PostAsJsonAsync(
            "api/v1/diagnostics/sync", report, Core.Contracts.ContractJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"云端返回 {(int)response.StatusCode}", null, response.StatusCode);
        }
    }

    private static Core.Contracts.SyncDiagnosticError ToContract(SyncErrorEntry entry) => new()
    {
        Code = entry.Code,
        Message = entry.Message,
        OccurredAt = ContractTime.Format(entry.OccurredAt),
    };

}
