namespace LiveQs.Windows.Core.Abstractions;

/// <summary>The running collector's own release version, as core semver X.Y.Z.</summary>
public interface IAppVersion
{
    string CurrentVersion
    {
        get;
    }
}
