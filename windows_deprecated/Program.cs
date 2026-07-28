using AiLife.WindowsAgent.Config;
using AiLife.WindowsAgent.Runtime;
using AiLife.WindowsAgent.Startup;
using AiLife.WindowsAgent.Ui;

namespace AiLife.WindowsAgent;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (StartupManager.IsStartupCommand(args))
        {
            return StartupManager.Execute(args, Console.Out, Console.Error);
        }

        if (HasArg(args, "--once") || HasArg(args, "--cli"))
        {
            return RunCliAsync(args).GetAwaiter().GetResult();
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var configPath = AgentConfig.ResolveConfigPath(args);
        var settingsRequested = false;
        while (true)
        {
            var config = LoadConfigForUi(args);
            if (settingsRequested)
            {
                settingsRequested = false;
                ShowSettings(config, configPath);
                config = LoadConfigForUi(args);
            }

            var validationError = AgentConfig.Validate(config);
            if (validationError is not null)
            {
                var saved = ShowSettings(config, configPath, validationError);
                if (!saved) return 0;
                continue;
            }

            using var context = new TrayApplicationContext(config, configPath);
            Application.Run(context);

            if (!context.SettingsRequested)
            {
                return 0;
            }

            settingsRequested = true;
        }
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        AgentConfig config;
        try
        {
            config = AgentConfig.Load(args);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"[windows-agent] Configuration error: {ex.Message}");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var runner = new AgentRunner(config);
        var runOnce = HasArg(args, "--once");
        Console.WriteLine($"[windows-agent] Reporting to {config.ServerUrl} every {config.HeartbeatIntervalSeconds}s as {config.DeviceId}");
        Console.WriteLine($"[windows-agent] Queue directory: {config.QueueDirectory}");

        while (!cancellation.IsCancellationRequested)
        {
            var result = await runner.TickAsync(cancellation.Token);
            if (!result.UploadSucceeded)
            {
                Console.Error.WriteLine($"[windows-agent] Upload failed: {result.ErrorMessage}");
            }
            else if (result.Sample is not null)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:HH:mm:ss}] {result.Sample.AppId} afk={result.Sample.IsAfk} idle={result.Sample.IdleSeconds:F0}s audio={result.Sample.IsAudioPlaying} fullscreen={result.Sample.IsFullscreen} sent={result.SentCount}");
            }

            if (runOnce) break;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(config.HeartbeatIntervalSeconds), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.WriteLine("[windows-agent] Stopped");
        return 0;
    }

    private static AgentConfig LoadConfigForUi(string[] args)
    {
        try
        {
            return AgentConfig.Load(args, requireDeviceToken: false);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Logger.Warning($"config load failed: {ex.Message}");
            return AgentConfig.CreateDefault();
        }
    }

    private static bool ShowSettings(AgentConfig config, string configPath, string? reason = null)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Logger.Warning($"settings required: {reason}");
        }

        using var form = new SettingsForm(config, configPath);
        return form.ShowDialog() == DialogResult.OK;
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}
