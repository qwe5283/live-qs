using LiveQs.Windows.Core;

namespace LiveQs.Windows.Infrastructure;

public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveQs",
            "Windows");
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "liveqs.db");
    public string LogPath => Path.Combine(DataDirectory, "liveqs.log");
}
