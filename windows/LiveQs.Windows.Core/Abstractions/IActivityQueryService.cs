using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Core.Abstractions;

public interface IActivityQueryService
{
    Task<DashboardSnapshot> GetDashboardAsync(DateRange range, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivitySegment>> GetTimelineAsync(
        DateRange range,
        CancellationToken cancellationToken = default);

    Task<TimelinePage> GetTimelinePageAsync(
        DateRange range,
        int pageSize,
        TimelineCursor? cursor = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default);
}
