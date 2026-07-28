using System.Text.Json;
using AiLife.WindowsAgent.Config;

namespace AiLife.WindowsAgent.Queueing;

public sealed class SpoolQueue
{
    private readonly string _queueFile;
    private readonly int _maxItems;

    public SpoolQueue(AgentConfig config)
    {
        Directory.CreateDirectory(config.QueueDirectory);
        _queueFile = Path.Combine(config.QueueDirectory, "heartbeats.ndjson");
        _maxItems = config.MaxQueuedHeartbeats;
    }

    public async Task EnqueueAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var item = new QueuedHeartbeat(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToString("O"),
            payloadJson);
        var line = JsonSerializer.Serialize(item, JsonOptions.Default);
        await File.AppendAllTextAsync(_queueFile, $"{line}{Environment.NewLine}", cancellationToken);
        await CompactIfNeededAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedHeartbeat>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_queueFile))
        {
            return [];
        }

        var items = new List<QueuedHeartbeat>();
        foreach (var line in await File.ReadAllLinesAsync(_queueFile, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<QueuedHeartbeat>(line, JsonOptions.Default);
                if (item is not null && !string.IsNullOrWhiteSpace(item.PayloadJson))
                {
                    items.Add(item);
                }
            }
            catch (JsonException)
            {
                // Skip corrupt queue lines instead of blocking uploads forever.
            }
        }

        return items;
    }

    public async Task ReplaceAsync(IReadOnlyList<QueuedHeartbeat> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            if (File.Exists(_queueFile))
            {
                File.Delete(_queueFile);
            }
            return;
        }

        var tempFile = $"{_queueFile}.tmp";
        var lines = items.Select(item => JsonSerializer.Serialize(item, JsonOptions.Default));
        await File.WriteAllLinesAsync(tempFile, lines, cancellationToken);
        File.Move(tempFile, _queueFile, true);
    }

    private async Task CompactIfNeededAsync(CancellationToken cancellationToken)
    {
        var items = await ReadAllAsync(cancellationToken);
        if (items.Count <= _maxItems) return;

        var retained = items.Skip(items.Count - _maxItems).ToArray();
        await ReplaceAsync(retained, cancellationToken);
    }
}

