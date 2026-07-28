using AiLife.WindowsAgent.Config;
using AiLife.WindowsAgent.Monitoring;
using AiLife.WindowsAgent.Queueing;
using AiLife.WindowsAgent.Reporting;

namespace AiLife.WindowsAgent.Runtime;

public sealed class AgentRunner : IDisposable
{
    private readonly SystemSampler _sampler = new();
    private readonly HeartbeatReporter _reporter;
    private readonly SpoolQueue _queue;
    private readonly QueueDrainer _drainer;

    public AgentRunner(AgentConfig config)
    {
        Config = config;
        _reporter = new HeartbeatReporter(config);
        _queue = new SpoolQueue(config);
        _drainer = new QueueDrainer(_queue, _reporter);
    }

    public AgentConfig Config { get; }

    public async Task<AgentTickResult> TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sample = _sampler.Capture(Config);
            if (sample is null)
            {
                return AgentTickResult.NoSample();
            }

            await _queue.EnqueueAsync(_reporter.CreatePayloadJson(sample), cancellationToken);
            var sent = await _drainer.DrainAsync(cancellationToken);
            return AgentTickResult.Success(sample, sent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return AgentTickResult.Failure(ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return AgentTickResult.Failure(ex.Message);
        }
        catch (IOException ex)
        {
            return AgentTickResult.Failure(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return AgentTickResult.Failure(ex.Message);
        }
    }

    public void Dispose() => _reporter.Dispose();
}

public sealed record AgentTickResult(
    bool HasSample,
    bool UploadSucceeded,
    ForegroundSample? Sample,
    int SentCount,
    string? ErrorMessage)
{
    public static AgentTickResult NoSample() => new(false, true, null, 0, null);

    public static AgentTickResult Success(ForegroundSample sample, int sentCount) =>
        new(true, true, sample, sentCount, null);

    public static AgentTickResult Failure(string message) =>
        new(false, false, null, 0, message);
}

