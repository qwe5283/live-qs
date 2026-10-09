namespace LiveQs.Windows.Core.Abstractions;

public interface IAppPaths
{
    string DataDirectory
    {
        get;
    }
    string DatabasePath
    {
        get;
    }
    string LogPath
    {
        get;
    }
}
