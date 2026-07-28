using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AiLife.WindowsAgent.Config;

namespace AiLife.WindowsAgent.Monitoring;

public sealed class SystemSampler
{
    private readonly AudioActivityDetector _audioActivityDetector = new();

    public ForegroundSample? Capture(AgentConfig config)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var title = config.WindowTitleMode == "none" ? "" : GetWindowTitle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        var processName = "unknown.exe";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : $"{process.ProcessName}.exe";
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        var idleSeconds = GetIdleSeconds();
        var battery = GetBattery();

        return new ForegroundSample(
            processName,
            processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName,
            title,
            idleSeconds,
            idleSeconds >= config.AfkThresholdSeconds,
            _audioActivityDetector.IsAudioPlaying(),
            IsForegroundFullscreen(hwnd),
            battery.Percent,
            battery.Charging);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Length);
        return copied <= 0 ? "" : new string(buffer, 0, copied).Trim();
    }

    private static double GetIdleSeconds()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };
        if (!NativeMethods.GetLastInputInfo(ref info)) return 0;

        var elapsedMs = (NativeMethods.GetTickCount() - info.dwTime) & 0xFFFFFFFF;
        return elapsedMs / 1000.0;
    }

    private static bool IsForegroundFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var width = NativeMethods.GetSystemMetrics(0);
        var height = NativeMethods.GetSystemMetrics(1);
        return rect.Left <= 0 && rect.Top <= 0 && rect.Right >= width && rect.Bottom >= height;
    }

    private static (int? Percent, bool? Charging) GetBattery()
    {
        try
        {
            var status = SystemInformation.PowerStatus;
            if (status.BatteryChargeStatus == BatteryChargeStatus.NoSystemBattery)
            {
                return (null, null);
            }
            return ((int)Math.Round(status.BatteryLifePercent * 100), status.PowerLineStatus == PowerLineStatus.Online);
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
    }
}

