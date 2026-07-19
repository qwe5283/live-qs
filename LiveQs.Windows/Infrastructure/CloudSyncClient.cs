using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using LiveQs.Windows.Core;

namespace LiveQs.Windows.Infrastructure;

public sealed class CloudSyncClient(IHttpClientFactory httpClientFactory) : ISyncClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UploadAsync(IReadOnlyList<SyncQueueItem> items, AppSettings settings, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        var client = httpClientFactory.CreateClient("cloud-sync");
        client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);

        var events = items.Select(item => new
        {
            idempotency_key = $"windows-segment-{settings.DeviceId}-{item.SegmentId}-{item.EndedAt.UtcTicks}",
            bucket = $"windows:{settings.DeviceId}:foreground",
            type = "app.foreground",
            start_at = item.StartedAt.ToUniversalTime().ToString("O"),
            end_at = item.EndedAt.ToUniversalTime().ToString("O"),
            data = new Dictionary<string, object?>
            {
                ["app_id"] = item.AppId,
                ["app_name"] = item.AppName,
                ["window_title_hash"] = string.IsNullOrWhiteSpace(item.WindowTitleHash) ? null : item.WindowTitleHash,
                ["title_present"] = !string.IsNullOrWhiteSpace(item.WindowTitle) || !string.IsNullOrWhiteSpace(item.WindowTitleHash),
                ["is_afk"] = item.IsAfk,
                ["is_audio_playing"] = item.IsAudioPlaying,
                ["is_fullscreen"] = item.IsFullscreen,
            },
        });

        using var response = await client.PostAsJsonAsync("api/v1/ingest/events", new { events }, JsonOptions, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 300) body = body[..300];
        throw new HttpRequestException($"云端返回 {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }
}
