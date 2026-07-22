using LiveQs.Windows.Core;
using Microsoft.Win32;

namespace LiveQs.Windows.Infrastructure;

public sealed class StartupManager : IStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiveQsWindows";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位程序文件。");
        var assemblyPath = Environment.GetCommandLineArgs().FirstOrDefault();
        var command = string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
                      assemblyPath?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true
            ? $"\"{processPath}\" \"{assemblyPath}\" --background"
            : $"\"{processPath}\" --background";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
