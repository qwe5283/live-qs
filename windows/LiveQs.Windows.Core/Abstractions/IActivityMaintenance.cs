using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Core.Abstractions;

public interface IActivityMaintenance
{
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task<int> DeleteRangeAsync(DateRange range, CancellationToken cancellationToken = default);

    Task ExportCsvAsync(string path, DateRange range, CancellationToken cancellationToken = default);

    Task OptimizeAsync(CancellationToken cancellationToken = default);
}
