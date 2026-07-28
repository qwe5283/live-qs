using AiLife.WindowsAgent.Reporting;
using System.Text.Json;

namespace AiLife.WindowsAgent.Queueing;

public sealed class QueueDrainer
{
    private static readonly TimeSpan MaxHeartbeatAge = TimeSpan.FromMinutes(5);
    private readonly SpoolQueue _queue;
    private readonly IHeartbeatSender _sender;

    public QueueDrainer(SpoolQueue queue, IHeartbeatSender sender)
    {
        _queue = queue;
        _sender = sender;
    }

    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var items = await _queue.ReadAllAsync(cancellationToken);
        if (items.Count == 0) return 0;

        var freshItems = items.Where(IsFresh).ToArray();
        if (freshItems.Length == 0)
        {
            await _queue.ReplaceAsync([], cancellationToken);
            return 0;
        }

        var sent = 0;
        for (var i = 0; i < freshItems.Length; i++)
        {
            try
            {
                await _sender.SendJsonAsync(freshItems[i].PayloadJson, cancellationToken);
                sent++;
            }
            catch
            {
                await _queue.ReplaceAsync(freshItems.Skip(i).ToArray(), CancellationToken.None);
                throw;
            }
        }

        await _queue.ReplaceAsync([], cancellationToken);
        return sent;
    }

    private static bool IsFresh(QueuedHeartbeat item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            if (!document.RootElement.TryGetProperty("timestamp", out var timestampElement))
            {
                return false;
            }

            var timestamp = timestampElement.GetString();
            if (!DateTimeOffset.TryParse(timestamp, out var parsed))
            {
                return false;
            }

            return DateTimeOffset.UtcNow - parsed.ToUniversalTime() <= MaxHeartbeatAge;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
