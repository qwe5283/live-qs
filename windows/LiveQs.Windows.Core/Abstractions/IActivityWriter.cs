using LiveQs.Windows.Core.Activity;

namespace LiveQs.Windows.Core.Abstractions;

public interface IActivityWriter
{
    Task RecordSampleAsync(
        ActivitySample sample,
        TimeSpan sampleInterval,
        CancellationToken cancellationToken = default);
}
