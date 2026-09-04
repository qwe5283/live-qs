using System.Net.Http.Json;
using System.Net.Http;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LiveQs.Windows.Infrastructure.Classification;

/// <summary>
/// Keeps the local rule cache close to the Owner's published version: it
/// re-fetches the rule set document at a bounded cadence (not on every sync
/// pass) and stores every successful response. A failed refresh — outage,
/// invalid response, missing token — is logged and the previous cache stays
/// authoritative, so classification keeps running offline with the last
/// successful version.
/// </summary>
public sealed class ClassificationRuleSync(
    IHttpClientFactory httpClientFactory,
    IClassificationRuleStore ruleStore,
    TimeProvider timeProvider,
    ILogger<ClassificationRuleSync> logger) : IClassificationRuleSync
{
    /// <summary>How long a cached rule set is trusted before the next refresh attempt.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;

    public async Task<Core.Contracts.ClassificationRuleSet?> RefreshAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastFetchUtc < RefreshInterval)
        {
            return await ruleStore.GetCachedRuleSetAsync(cancellationToken);
        }
        _lastFetchUtc = now;

        try
        {
            var client = httpClientFactory.CreateClient("cloud-sync");
            client.BaseAddress = new Uri($"{settings.ServerBaseUrl.TrimEnd('/')}/");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.DeviceToken);

            using var response = await client.GetAsync(
                "api/v1/classification/ruleset", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Classification rule refresh returned {StatusCode}; keeping the cached rule set.", (int)response.StatusCode);
                return await ruleStore.GetCachedRuleSetAsync(cancellationToken);
            }
            var ruleSet = await response.Content.ReadFromJsonAsync<Core.Contracts.ClassificationRuleSet>(
                Core.Contracts.ContractJson.Options, cancellationToken);
            if (ruleSet is null)
            {
                logger.LogWarning("Classification rule refresh returned an empty document; keeping the cached rule set.");
                return await ruleStore.GetCachedRuleSetAsync(cancellationToken);
            }
            await ruleStore.SaveCachedRuleSetAsync(ruleSet, now, cancellationToken);
            return ruleSet;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning(exception, "Classification rule refresh failed; keeping the cached rule set for offline execution.");
            return await ruleStore.GetCachedRuleSetAsync(cancellationToken);
        }
    }
}
