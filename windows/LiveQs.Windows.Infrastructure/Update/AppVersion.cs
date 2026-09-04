using System.Reflection;
using LiveQs.Windows.Core.Abstractions;

namespace LiveQs.Windows.Infrastructure.Update;

/// <summary>The running collector's own release version, taken from the entry assembly.</summary>
public sealed class AppVersion : IAppVersion
{
    public static readonly AppVersion Instance = new();

    public string CurrentVersion { get; } = Resolve();

    private static string Resolve()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
