using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Core.Abstractions;

public interface ISettingsStore
{
    Task SaveApplicationRuleAsync(ApplicationRule rule, CancellationToken cancellationToken = default);

    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
