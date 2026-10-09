using System.Net.Http.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Http;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Uploads one heartbeat through the current-state contract
/// (POST /api/v1/heartbeats) with a Device Token. Heartbeats are ephemeral
/// projections: they never enter the events outbox and carry no raw titles
/// or executable paths.
/// </summary>
public sealed class HeartbeatClient(IHttpClientFactory httpClientFactory) : IHeartbeatClient
{
    public async Task SendAsync(
        HeartbeatState state,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var client = CloudApiClient.Create(httpClientFactory, settings);

        var request = new Core.Contracts.HeartbeatRequest
        {
            Platform = Core.Contracts.Platform.Windows,
            DeviceName = settings.DeviceId,
            CapturedAt = ContractTime.Format(state.CapturedAt),
            Activity = new Core.Contracts.HeartbeatActivity
            {
                ApplicationId = state.ApplicationId,
                ApplicationLabel = string.IsNullOrWhiteSpace(state.ApplicationLabel) ? null : state.ApplicationLabel,
                IsAfk = state.IsAfk,
            },
        };
        using var response = await client.PostAsJsonAsync(
            "api/v1/heartbeats", request, Core.Contracts.ContractJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 300)
            {
                body = body[..300];
            }

            throw new HttpRequestException($"云端返回 {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }
    }

}
