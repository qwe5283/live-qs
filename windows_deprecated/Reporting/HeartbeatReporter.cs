using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLife.WindowsAgent.Config;
using AiLife.WindowsAgent.Monitoring;

namespace AiLife.WindowsAgent.Reporting;

public sealed class HeartbeatReporter : IHeartbeatSender, IDisposable
{
    private readonly HttpClient _client = new();
    private readonly AgentConfig _config;

    public HeartbeatReporter(AgentConfig config)
    {
        _config = config;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DeviceToken);
        _client.Timeout = TimeSpan.FromSeconds(10);
    }

    public string CreatePayloadJson(ForegroundSample sample)
    {
        var data = new Dictionary<string, object?>
        {
            ["app_id"] = sample.AppId,
            ["app_name"] = sample.AppName,
            ["is_afk"] = sample.IsAfk,
            ["idle_seconds"] = (int)Math.Round(sample.IdleSeconds),
            ["is_audio_playing"] = sample.IsAudioPlaying,
            ["is_fullscreen"] = sample.IsFullscreen,
        };

        if (_config.WindowTitleMode == "raw")
        {
            data["window_title"] = sample.WindowTitle;
        }
        else if (_config.WindowTitleMode == "hash" && !string.IsNullOrWhiteSpace(sample.WindowTitle))
        {
            data["window_title_hash"] = Sha256(sample.WindowTitle.Trim().ToLowerInvariant());
            data["title_present"] = true;
        }
        if (sample.BatteryPercent is not null)
        {
            data["battery_percent"] = sample.BatteryPercent;
        }
        if (sample.BatteryCharging is not null)
        {
            data["battery_charging"] = sample.BatteryCharging;
        }

        var payload = new
        {
            bucket = $"windows:{_config.DeviceId}:foreground",
            type = "app.foreground",
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            heartbeat_interval_ms = _config.HeartbeatIntervalSeconds * 1000,
            data,
        };

        return JsonSerializer.Serialize(payload, JsonOptions.Default);
    }

    public async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"{_config.ServerUrl}/api/v1/ingest/heartbeat", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

