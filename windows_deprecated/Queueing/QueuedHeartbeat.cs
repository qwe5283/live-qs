namespace AiLife.WindowsAgent.Queueing;

public sealed record QueuedHeartbeat(
    string Id,
    string CreatedAt,
    string PayloadJson);

