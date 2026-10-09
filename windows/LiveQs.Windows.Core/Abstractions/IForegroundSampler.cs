using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Core.Abstractions;

public interface IForegroundSampler
{
    ActivitySample? Capture(AppSettings settings);
}
