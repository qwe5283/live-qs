using System.Text.Json;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Contracts;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    /// <summary>The cached rule set of the last successful fetch; survives outages for offline classification.</summary>
    public async Task<ClassificationRuleSet?> GetCachedRuleSetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM classification_cache WHERE id = 1;";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try
        {
            return JsonSerializer.Deserialize<ClassificationRuleSet>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            // A damaged cache degrades to "no rules known", never to a crash.
            return null;
        }
    }

    public async Task SaveCachedRuleSetAsync(ClassificationRuleSet ruleSet, DateTimeOffset fetchedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO classification_cache(id, rule_set_version, json, fetched_utc)
            VALUES(1, $version, $json, $now)
            ON CONFLICT(id) DO UPDATE SET
                rule_set_version = excluded.rule_set_version,
                json = excluded.json,
                fetched_utc = excluded.fetched_utc;
            """;
        command.Parameters.AddWithValue("$version", ruleSet.RuleSetVersion);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(ruleSet, ContractJson.Options));
        command.Parameters.AddWithValue("$now", UtcText(fetchedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The per-installation classification secret, generated on first use and never rotated.</summary>
    public async Task<string> GetClassificationSecretAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT classification_secret FROM sync_state WHERE id = 1;";
        var value = (await command.ExecuteScalarAsync(cancellationToken)) as string;
        return value ?? "";
    }
}
