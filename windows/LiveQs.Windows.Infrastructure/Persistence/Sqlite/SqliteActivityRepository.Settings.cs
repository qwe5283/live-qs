using System.Text.Json;
using LiveQs.Windows.Core.Settings;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    public async Task<IReadOnlyList<ApplicationRule>> GetApplicationRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT known.app_id,
                   COALESCE(r.alias, ''),
                   COALESCE(r.category, '未分类'),
                   COALESCE(r.is_excluded, 0)
            FROM (SELECT DISTINCT app_id FROM activity_segments) known
            LEFT JOIN application_rules r ON r.app_id = known.app_id
            ORDER BY known.app_id COLLATE NOCASE;
            """;
        var result = new List<ApplicationRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ApplicationRule(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return result;
    }

    public async Task SaveApplicationRuleAsync(ApplicationRule rule, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO application_rules(app_id, alias, category, is_excluded, updated_utc)
            VALUES($appId, $alias, $category, $excluded, $now)
            ON CONFLICT(app_id) DO UPDATE SET
                alias = excluded.alias, category = excluded.category,
                is_excluded = excluded.is_excluded, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$appId", rule.AppId.Trim());
        command.Parameters.AddWithValue("$alias", rule.Alias.Trim());
        command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(rule.Category) ? "未分类" : rule.Category.Trim());
        command.Parameters.AddWithValue("$excluded", rule.IsExcluded);
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM app_settings WHERE id = 1;";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try { return (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings()).Normalize(); }
        catch (JsonException) { return new AppSettings(); }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        var error = normalized.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(settings));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(id, json, updated_utc)
            VALUES(1, $json, $now)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(normalized, JsonOptions));
        command.Parameters.AddWithValue("$now", UtcText(_timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ApplicationRule?> GetRuleAsync(string appId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id, alias, category, is_excluded FROM application_rules WHERE app_id = $appId;";
        command.Parameters.AddWithValue("$appId", appId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ApplicationRule(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }
}
