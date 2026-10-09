using LiveQs.Windows.Core.Abstractions;

namespace LiveQs.Windows.Tests;

internal sealed class StubAppVersion(string version) : IAppVersion
{
    public string CurrentVersion { get; } = version;
}
