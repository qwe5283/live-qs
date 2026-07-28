using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Infrastructure.Windows;

namespace LiveQs.Windows.Infrastructure.Sampling;

public sealed class ForegroundSampler : IForegroundSampler
{
    private readonly TimeProvider _timeProvider;
    private readonly AudioActivityDetector _audioDetector = new();

    public ForegroundSampler(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ActivitySample? Capture(AppSettings settings)
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == nint.Zero) return null;

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;

        var processName = "unknown.exe";
        var displayName = "未知应用";
        var executablePath = "";
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : $"{process.ProcessName}.exe";
            executablePath = TryGetExecutablePath(process);
            displayName = GetDisplayName(processName, executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            displayName = Path.GetFileNameWithoutExtension(processName);
        }

        var rawTitle = ReadWindowTitle(handle);
        var titleHash = string.IsNullOrWhiteSpace(rawTitle) ? "" : Sha256(rawTitle.Trim().ToLowerInvariant());
        var visibleTitle = settings.WindowTitleMode == WindowTitleMode.Original ? rawTitle : "";
        if (settings.WindowTitleMode == WindowTitleMode.None) titleHash = "";
        var idleSeconds = GetIdleSeconds();
        var battery = GetBattery();

        return new ActivitySample(
            _timeProvider.GetUtcNow(),
            processName.ToLowerInvariant(),
            displayName,
            executablePath,
            visibleTitle,
            titleHash,
            idleSeconds,
            idleSeconds >= settings.AfkThresholdSeconds,
            _audioDetector.IsAudioPlaying(),
            IsFullscreen(handle),
            battery.Percent,
            battery.Charging);
    }

    private static string TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName ?? ""; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return ""; }
    }

    private static string GetDisplayName(string processName, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var value = info.FileDescription ?? info.ProductName;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch (FileNotFoundException) { }
        }
        return Path.GetFileNameWithoutExtension(processName);
    }

    private static string ReadWindowTitle(nint handle)
    {
        var length = Math.Min(NativeMethods.GetWindowTextLength(handle), 1024);
        if (length <= 0) return "";
        var chars = new char[length + 1];
        var copied = NativeMethods.GetWindowText(handle, chars, chars.Length);
        return copied <= 0 ? "" : new string(chars, 0, copied).Trim();
    }

    private static double GetIdleSeconds()
    {
        var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return 0;
        var elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return elapsed / 1000d;
    }

    private static bool IsFullscreen(nint handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var window)) return false;
        var monitorHandle = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var monitor = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitorHandle == nint.Zero || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitor)) return false;
        const int tolerance = 2;
        return window.Left <= monitor.Monitor.Left + tolerance && window.Top <= monitor.Monitor.Top + tolerance &&
               window.Right >= monitor.Monitor.Right - tolerance && window.Bottom >= monitor.Monitor.Bottom - tolerance;
    }

    private static (int? Percent, bool? Charging) GetBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryFlag is 128 or byte.MaxValue)
            return (null, null);

        int? percent = status.BatteryLifePercent == byte.MaxValue ? null : status.BatteryLifePercent;
        bool? charging = status.AcLineStatus == byte.MaxValue ? null : status.AcLineStatus == 1;
        return (percent, charging);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
