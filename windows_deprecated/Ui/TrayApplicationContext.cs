using System.Diagnostics;
using AiLife.WindowsAgent.Config;
using AiLife.WindowsAgent.Monitoring;
using AiLife.WindowsAgent.Runtime;
using AiLife.WindowsAgent.Startup;

namespace AiLife.WindowsAgent.Ui;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _configPath;
    private readonly AgentRunner _runner;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Threading.Timer _timer;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly SynchronizationContext _uiContext;
    private readonly Icon _greenIcon;
    private readonly Icon _orangeIcon;
    private readonly Icon _redIcon;
    private readonly Icon _grayIcon;

    private AgentConfig _config;
    private string _status = "初始化";
    private string _current = "无";
    private string _lastMessage = "尚未上报";

    public TrayApplicationContext(AgentConfig config, string configPath)
    {
        _configPath = configPath;
        _config = config;
        _runner = new AgentRunner(config);
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Logger.SetFileLogging(config.EnableLog);
        Logger.Info($"Windows agent started: {config.ServerUrl}");

        _greenIcon = CreateCircleIcon(Color.FromArgb(43, 150, 91));
        _orangeIcon = CreateCircleIcon(Color.FromArgb(230, 142, 43));
        _redIcon = CreateCircleIcon(Color.FromArgb(205, 65, 65));
        _grayIcon = CreateCircleIcon(Color.FromArgb(117, 117, 117));

        BuildMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = _grayIcon,
            Text = "AI Life Windows Agent",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenSettings();
            }
        };

        _timer = new System.Threading.Timer(
            Tick,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(Math.Max(1, config.HeartbeatIntervalSeconds)));
    }

    public bool SettingsRequested { get; private set; }

    private void BuildMenu()
    {
        var statusItem = new ToolStripMenuItem { Enabled = false };
        var currentItem = new ToolStripMenuItem { Enabled = false };
        var lastItem = new ToolStripMenuItem { Enabled = false };

        _menu.Items.Add(statusItem);
        _menu.Items.Add(currentItem);
        _menu.Items.Add(lastItem);
        _menu.Items.Add(new ToolStripSeparator());

        var reportNowItem = new ToolStripMenuItem("立即上报");
        reportNowItem.Click += (_, _) => Tick(null);
        _menu.Items.Add(reportNowItem);

        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => OpenSettings();
        _menu.Items.Add(settingsItem);

        var openConfigItem = new ToolStripMenuItem("打开配置文件");
        openConfigItem.Click += (_, _) => OpenPath(_configPath);
        _menu.Items.Add(openConfigItem);

        var openQueueItem = new ToolStripMenuItem("打开队列目录");
        openQueueItem.Click += (_, _) => OpenPath(_runner.Config.QueueDirectory);
        _menu.Items.Add(openQueueItem);

        _menu.Items.Add(new ToolStripSeparator());

        var logItem = new ToolStripMenuItem("写入日志")
        {
            CheckOnClick = true,
            Checked = _config.EnableLog,
        };
        logItem.Click += (_, _) => ToggleLog(logItem);
        _menu.Items.Add(logItem);

        var autostartItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsAutostartEnabled(),
        };
        autostartItem.Click += (_, _) => ToggleAutostart(autostartItem);
        _menu.Items.Add(autostartItem);

        _menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("退出");
        quitItem.Click += (_, _) => Quit();
        _menu.Items.Add(quitItem);

        _menu.Opening += (_, _) =>
        {
            statusItem.Text = $"状态: {_status}";
            currentItem.Text = $"当前: {_current}";
            lastItem.Text = $"最近: {_lastMessage}";
            logItem.Checked = _config.EnableLog;
            autostartItem.Checked = StartupManager.IsAutostartEnabled();
        };
    }

    private async void Tick(object? state)
    {
        if (!_tickGate.Wait(0)) return;

        try
        {
            var result = await _runner.TickAsync(_cancellation.Token);
            _uiContext.Post(_ => ApplyResult(result), null);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private void ApplyResult(AgentTickResult result)
    {
        if (!result.UploadSucceeded)
        {
            _status = "上传失败";
            _lastMessage = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "上传失败" : result.ErrorMessage;
            _trayIcon.Icon = _redIcon;
            UpdateTooltip();
            Logger.Warning(_lastMessage);
            return;
        }

        if (!result.HasSample || result.Sample is null)
        {
            _status = "等待采样";
            _lastMessage = DateTime.Now.ToString("HH:mm:ss");
            _trayIcon.Icon = _grayIcon;
            UpdateTooltip();
            return;
        }

        _status = result.Sample.IsAfk ? "AFK" : "在线";
        _current = FormatCurrent(result.Sample);
        _lastMessage = $"{DateTime.Now:HH:mm:ss} sent={result.SentCount}";
        _trayIcon.Icon = result.Sample.IsAfk ? _orangeIcon : _greenIcon;
        UpdateTooltip();
        Logger.Info($"{_status}: {_current} {_lastMessage}");
    }

    private void UpdateTooltip()
    {
        var text = $"AI Life Windows Agent\n状态: {_status}\n当前: {_current}";
        _trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void ToggleLog(ToolStripMenuItem item)
    {
        var next = _config with { EnableLog = item.Checked };
        try
        {
            AgentConfig.Save(_configPath, next);
            _config = next;
            Logger.SetFileLogging(next.EnableLog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Checked = !item.Checked;
            MessageBox.Show($"无法保存日志设置: {ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleAutostart(ToolStripMenuItem item)
    {
        if (!StartupManager.SetAutostart(item.Checked, _configPath))
        {
            item.Checked = !item.Checked;
            MessageBox.Show("无法更新开机自启设置。", "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSettings()
    {
        SettingsRequested = true;
        Shutdown();
        ExitThread();
    }

    private void Quit()
    {
        SettingsRequested = false;
        Shutdown();
        ExitThread();
    }

    private void Shutdown()
    {
        _cancellation.Cancel();
        _timer.Dispose();
        _trayIcon.Visible = false;
        Logger.Shutdown();
    }

    private static string FormatCurrent(ForegroundSample sample)
    {
        var title = sample.WindowTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return sample.AppId;
        }

        if (title.Length > 72)
        {
            title = title[..72];
        }

        return $"{sample.AppId} - {title}";
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Icon CreateCircleIcon(Color color)
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 8, 8, 48, 48);
        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation.Dispose();
            _timer.Dispose();
            _tickGate.Dispose();
            _runner.Dispose();
            _trayIcon.Dispose();
            _menu.Dispose();
            _greenIcon.Dispose();
            _orangeIcon.Dispose();
            _redIcon.Dispose();
            _grayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}

