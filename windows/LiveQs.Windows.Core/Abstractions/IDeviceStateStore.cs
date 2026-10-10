using LiveQs.Windows.Core.Activity;

namespace LiveQs.Windows.Core.Abstractions;

public sealed record DeviceStateImportStatus(DateTimeOffset? ImportedUntil, string Warning);

public interface IDeviceStateStore
{
    Task AppendDeviceStateEventsAsync(IReadOnlyList<DeviceStateEvent> events, CancellationToken cancellationToken = default);
    Task CompleteDeviceStateImportAsync(IReadOnlyList<DeviceStateEvent> events, DateTimeOffset until,
        CancellationToken cancellationToken = default);
    Task<DeviceStateImportStatus> GetDeviceStateImportStatusAsync(CancellationToken cancellationToken = default);
    Task SetDeviceStateImportWarningAsync(string warning, CancellationToken cancellationToken = default);
}

public interface ISessionStateProvider
{
    bool IsLocked
    {
        get;
    }
}
