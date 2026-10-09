using LiveQs.Windows.Core.Contracts;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Core.Abstractions;

/// <summary>
/// Refreshes the cached rule set from the service at a bounded cadence. A
/// failed refresh is never fatal: the last successful version stays cached
/// and executable. A reclassification pass forces a refresh when its cached
/// version is older than the task's target version.
/// </summary>
public interface IClassificationRuleSync
{
    Task<ClassificationRuleSet?> RefreshAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false);
}
