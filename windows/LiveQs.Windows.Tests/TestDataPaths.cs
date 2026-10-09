using LiveQs.Windows.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Tests;

internal sealed class TestDataPaths : IAppPaths, IDisposable
{
    public TestDataPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "LiveQs.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory
    {
        get;
    }
    public string DatabasePath => Path.Combine(DataDirectory, "test.db");
    public string LogPath => Path.Combine(DataDirectory, "test.log");

    public void Dispose()
    {
        // A pooled SQLite handle otherwise keeps the isolated test directory open on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, true);
        }
    }
}
