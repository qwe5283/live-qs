namespace LiveQs.Windows.Core.Abstractions;

/// <summary>
/// The device current state asserted by one heartbeat: an ephemeral, expiring
/// projection, never a historical event and never part of the events outbox.
/// </summary>
public sealed record HeartbeatState(
    DateTimeOffset CapturedAt,
    string ApplicationId,
    string ApplicationLabel,
    bool IsAfk);
