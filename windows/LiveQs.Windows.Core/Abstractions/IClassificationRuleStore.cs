using LiveQs.Windows.Core.Contracts;

namespace LiveQs.Windows.Core.Abstractions;

/// <summary>
/// Local persistence of the Owner's classification rule set: the cache of the
/// last successfully fetched version survives outages so classification keeps
/// working offline, plus the per-installation secret that keys opaque
/// identifiers of locally discovered project names (the secret never leaves
/// the device).
/// </summary>
public interface IClassificationRuleStore
{
    Task<ClassificationRuleSet?> GetCachedRuleSetAsync(CancellationToken cancellationToken = default);

    Task SaveCachedRuleSetAsync(
        ClassificationRuleSet ruleSet,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken = default);

    Task<string> GetClassificationSecretAsync(CancellationToken cancellationToken = default);
}
