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
        // Release this database's pooled handle without interrupting other parallel tests.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, true);
        }
    }
}
