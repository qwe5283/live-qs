using System.Text;
using LiveQs.Windows.Core.Common;

namespace LiveQs.Windows.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteActivityRepository
{
    public async Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM excluded_sampling WHERE captured_utc < $cutoff;
            DELETE FROM activity_segments WHERE ended_utc < $cutoff;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$cutoff", UtcText(cutoff));
        var deleted = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        transaction.Commit();
        return deleted;
    }

    public async Task<int> DeleteRangeAsync(DateRange range, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM excluded_sampling WHERE captured_utc >= $start AND captured_utc < $end;
            DELETE FROM activity_segments WHERE started_utc < $end AND ended_utc > $start;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$start", UtcText(range.Start));
        command.Parameters.AddWithValue("$end", UtcText(range.End));
        var deleted = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        transaction.Commit();
        return deleted;
    }

    public async Task ExportCsvAsync(string path, DateRange range, CancellationToken cancellationToken = default)
    {
        var rows = await ReadSegmentsAsync(range, cancellationToken);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("开始时间,结束时间,时长秒,应用ID,应用名称,分类,窗口标题,AFK,音频,全屏".AsMemory(), cancellationToken);
        foreach (var row in rows)
        {
            var values = new[]
            {
                row.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                row.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                ((long)row.Duration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.AppId, row.AppName, row.Category, row.WindowTitle,
                row.IsAfk ? "是" : "否", row.IsAudioPlaying ? "是" : "否", row.IsFullscreen ? "是" : "否",
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(Csv)).AsMemory(), cancellationToken);
        }
    }

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
