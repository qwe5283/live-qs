using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLife.WindowsAgent.Config;

public sealed record AgentConfig(
    string ServerUrl,
    string DeviceToken,
    string DeviceId,
    string DeviceName,
    int HeartbeatIntervalSeconds,
    int AfkThresholdSeconds,
    string WindowTitleMode,
    string QueueDirectory,
    int MaxQueuedHeartbeats,
    bool EnableLog = false)
{
    public static AgentConfig CreateDefault() =>
        new(
            "http://localhost:8787",
            "",
            "desktop",
            Environment.MachineName,
            5,
            60,
            "hash",
            ResolveQueueDirectory(null),
            10_000,
            false);

    public static AgentConfig Load(string[] args) => Load(args, requireDeviceToken: true);

    public static AgentConfig Load(string[] args, bool requireDeviceToken)
    {
        var configPath = ResolveConfigPath(args);
        FileConfig fileConfig = new();

        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            fileConfig = JsonSerializer.Deserialize<FileConfig>(json, JsonOptions.Default) ?? new FileConfig();
        }

        var serverUrl = Env("AI_LIFE_SERVER_URL", fileConfig.ServerUrl) ?? "http://localhost:8787";
        var token = Env("AI_LIFE_DEVICE_TOKEN", fileConfig.DeviceToken);
        if (requireDeviceToken && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Device token is required. Set AI_LIFE_DEVICE_TOKEN or deviceToken in config.");
        }

        return new AgentConfig(
            NormalizeServerUrl(serverUrl),
            token ?? "",
            Env("AI_LIFE_DEVICE_ID", fileConfig.DeviceId) ?? "desktop",
            Env("AI_LIFE_DEVICE_NAME", fileConfig.DeviceName) ?? Environment.MachineName,
            ClampInt(EnvInt("AI_LIFE_HEARTBEAT_SECONDS", fileConfig.HeartbeatIntervalSeconds), 1, 3600, 5),
            ClampInt(EnvInt("AI_LIFE_AFK_THRESHOLD_SECONDS", fileConfig.AfkThresholdSeconds), 5, 24 * 3600, 60),
            ResolveWindowTitleMode(fileConfig),
            ResolveQueueDirectory(Env("AI_LIFE_QUEUE_DIR", fileConfig.QueueDirectory)),
            ClampInt(EnvInt("AI_LIFE_MAX_QUEUED_HEARTBEATS", fileConfig.MaxQueuedHeartbeats), 1, 100_000, 10_000),
            EnvBool("AI_LIFE_ENABLE_LOG", fileConfig.EnableLog) ?? false);
    }

    public static string ResolveConfigPath(string[] args)
    {
        var explicitPath = GetArg(args, "--config") ?? Environment.GetEnvironmentVariable("AI_LIFE_WINDOWS_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath));
        }

        return Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    public static bool Save(string path, AgentConfig config)
    {
        var fileConfig = new FileConfig
        {
            ServerUrl = NormalizeServerUrl(config.ServerUrl),
            DeviceToken = config.DeviceToken.Trim(),
            DeviceId = config.DeviceId.Trim(),
            DeviceName = config.DeviceName.Trim(),
            HeartbeatIntervalSeconds = config.HeartbeatIntervalSeconds,
            AfkThresholdSeconds = config.AfkThresholdSeconds,
            WindowTitleMode = ResolveWindowTitleMode(config.WindowTitleMode) ?? "hash",
            QueueDirectory = config.QueueDirectory,
            MaxQueuedHeartbeats = config.MaxQueuedHeartbeats,
            EnableLog = config.EnableLog,
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? AppContext.BaseDirectory, $".config_{Path.GetRandomFileName()}.tmp");
        var json = JsonSerializer.Serialize(fileConfig, JsonOptions.Pretty);
        File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
        return true;
    }

    public static string? Validate(AgentConfig config)
    {
        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return "服务器地址必须是有效的 http:// 或 https:// URL。";
        }

        if (string.IsNullOrWhiteSpace(config.DeviceToken))
        {
            return "Device Token 不能为空。";
        }

        if (string.IsNullOrWhiteSpace(config.DeviceId))
        {
            return "Device ID 不能为空。";
        }

        if (config.HeartbeatIntervalSeconds is < 1 or > 3600)
        {
            return "上报间隔必须在 1 到 3600 秒之间。";
        }

        if (config.AfkThresholdSeconds is < 5 or > 86400)
        {
            return "AFK 判定必须在 5 到 86400 秒之间。";
        }

        if (ResolveWindowTitleMode(config.WindowTitleMode) is null)
        {
            return "窗口标题模式必须是 none、hash 或 raw。";
        }

        if (string.IsNullOrWhiteSpace(config.QueueDirectory))
        {
            return "队列目录不能为空。";
        }

        if (config.MaxQueuedHeartbeats is < 1 or > 100_000)
        {
            return "最大离线队列必须在 1 到 100000 条之间。";
        }

        return null;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string NormalizeServerUrl(string value) => value.Trim().TrimEnd('/');

    private static string? Env(string name, string? fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int? EnvInt(string name, int? fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool? EnvBool(string name, bool? fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value == "1") return true;
        if (value == "0") return false;
        return fallback;
    }

    private static int ClampInt(int? value, int min, int max, int fallback)
    {
        if (value is null) return fallback;
        return Math.Min(max, Math.Max(min, value.Value));
    }

    private static string ResolveWindowTitleMode(FileConfig fileConfig)
    {
        var mode = Env("AI_LIFE_WINDOW_TITLE_MODE", fileConfig.WindowTitleMode);
        var resolved = ResolveWindowTitleMode(mode);
        if (resolved is not null) return resolved;

        var includeRawTitle = EnvBool("AI_LIFE_INCLUDE_WINDOW_TITLE", fileConfig.IncludeWindowTitle);
        if (includeRawTitle is true) return "raw";
        if (includeRawTitle is false) return "none";
        return "hash";
    }

    private static string? ResolveWindowTitleMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "none" or "hash" or "raw" ? normalized : null;
    }

    private static string ResolveQueueDirectory(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AiLife", "WindowsAgent");
    }

    private sealed class FileConfig
    {
        [JsonPropertyName("serverUrl")]
        public string? ServerUrl { get; set; }
        [JsonPropertyName("deviceToken")]
        public string? DeviceToken { get; set; }
        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }
        [JsonPropertyName("deviceName")]
        public string? DeviceName { get; set; }
        [JsonPropertyName("heartbeatIntervalSeconds")]
        public int? HeartbeatIntervalSeconds { get; set; }
        [JsonPropertyName("afkThresholdSeconds")]
        public int? AfkThresholdSeconds { get; set; }
        [JsonPropertyName("windowTitleMode")]
        public string? WindowTitleMode { get; set; }
        [JsonPropertyName("includeWindowTitle")]
        public bool? IncludeWindowTitle { get; set; }
        [JsonPropertyName("queueDirectory")]
        public string? QueueDirectory { get; set; }
        [JsonPropertyName("maxQueuedHeartbeats")]
        public int? MaxQueuedHeartbeats { get; set; }
        [JsonPropertyName("enableLog")]
        public bool? EnableLog { get; set; }
    }
}

