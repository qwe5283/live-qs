using System.Reflection;
using Microsoft.Win32;

namespace AiLife.WindowsAgent.Startup;

public static class StartupManager
{
    public const string RegistryValueName = "AiLifeWindowsAgent";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsStartupCommand(string[] args) =>
        HasArg(args, "--install-startup") || HasArg(args, "--uninstall-startup") || HasArg(args, "--startup-status");

    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (HasArg(args, "--install-startup"))
        {
            var command = BuildStartupCommand(args);
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error.WriteLine("[windows-agent] Unable to open HKCU startup registry key.");
                return 1;
            }

            key.SetValue(RegistryValueName, command, RegistryValueKind.String);
            output.WriteLine($"[windows-agent] Installed user startup entry: {RegistryValueName}");
            output.WriteLine(command);
            return 0;
        }

        if (HasArg(args, "--uninstall-startup"))
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
            output.WriteLine($"[windows-agent] Removed user startup entry: {RegistryValueName}");
            return 0;
        }

        if (HasArg(args, "--startup-status"))
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = key?.GetValue(RegistryValueName) as string;
            if (string.IsNullOrWhiteSpace(command))
            {
                output.WriteLine("[windows-agent] Startup entry is not installed.");
                return 1;
            }

            output.WriteLine("[windows-agent] Startup entry is installed:");
            output.WriteLine(command);
            return 0;
        }

        return 1;
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RegistryValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool SetAutostart(bool enabled, string configPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var command = BuildStartupCommand(["--config", configPath]);
                key.SetValue(RegistryValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static string BuildStartupCommand(string[] args, string? processPath = null, string? assemblyPath = null)
    {
        processPath ??= Environment.ProcessPath;
        assemblyPath ??= Assembly.GetEntryAssembly()?.Location;

        var configPath = GetArg(args, "--config");
        var commandParts = new List<string>();

        if (IsDotnetHost(processPath) && !string.IsNullOrWhiteSpace(assemblyPath) && assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            commandParts.Add(Quote(processPath!));
            commandParts.Add(Quote(assemblyPath));
        }
        else
        {
            commandParts.Add(Quote(processPath ?? assemblyPath ?? throw new InvalidOperationException("Cannot resolve process path.")));
        }

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            commandParts.Add("--config");
            commandParts.Add(Quote(Path.GetFullPath(Environment.ExpandEnvironmentVariables(configPath))));
        }

        return string.Join(" ", commandParts);
    }

    private static bool HasArg(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

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

    private static bool IsDotnetHost(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        var fileName = Path.GetFileName(processPath);
        return string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

