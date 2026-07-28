namespace AiLife.WindowsAgent.Reporting;

public interface IHeartbeatSender
{
    Task SendJsonAsync(string json, CancellationToken cancellationToken);
}

