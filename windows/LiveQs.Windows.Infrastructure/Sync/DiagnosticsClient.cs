using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;

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
    public async Task PushAsync(SyncDiagnosticsSnapshot snapshot, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("cloud-sync");
        client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);

        var report = new Core.Contracts.SyncDiagnosticsReport
        {
            Platform = Core.Contracts.Platform.Windows,
            DeviceName = settings.DeviceId,
            CollectedAt = WireInstant(snapshot.CollectedAt),
            LastSuccessfulUploadAt = WireInstant(snapshot.LastSuccessfulUploadAt),
            OldestPendingAt = WireInstant(snapshot.OldestPendingAt),
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
        OccurredAt = WireInstant(entry.OccurredAt),
    };

    private static string WireInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? WireInstant(DateTimeOffset? value) => value is null
        ? null
        : WireInstant(value.Value);
}
