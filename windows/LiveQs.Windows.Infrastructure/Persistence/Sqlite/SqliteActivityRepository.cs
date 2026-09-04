using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Core.Activity;
using LiveQs.Windows.Core.Analytics;
using LiveQs.Windows.Core.Common;
using LiveQs.Windows.Core.Settings;
using LiveQs.Windows.Core.Sync;
using Microsoft.Data.Sqlite;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository :
    IDatabaseInitializer,
    IActivityWriter,
    IActivityQueryService,
    ISettingsStore,
    ISyncQueueStore,
    IClassificationRuleStore,
    IActivityMaintenance
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] Palette =
    [
        "#007AFF", "#D56A45", "#4285F4", "#1DB954", "#7A63A8",
        "#4A154B", "#A75B2D", "#A259FF", "#FF6F00", "#526D82",
    ];

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public SqliteActivityRepository(IAppPaths paths, TimeProvider timeProvider)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        _timeProvider = timeProvider;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static TimeSpan ClippedDuration(DateTimeOffset startedAt, DateTimeOffset endedAt, DateRange range)
    {
        var start = startedAt > range.Start ? startedAt : range.Start;
        var end = endedAt < range.End ? endedAt : range.End;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private static string Fingerprint(ActivitySample sample, WindowTitleMode mode)
    {
        var title = mode switch
        {
            WindowTitleMode.Original => sample.WindowTitle,
            WindowTitleMode.Hash => sample.WindowTitleHash,
            _ => "",
        };
        var value = $"{sample.AppId}\n{title}\n{sample.IsAfk}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ColorFor(string appId)
    {
        uint hash = 2166136261;
        foreach (var character in appId)
        {
            hash ^= char.ToLowerInvariant(character);
            hash *= 16777619;
        }
        return Palette[hash % Palette.Length];
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string UtcText(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
