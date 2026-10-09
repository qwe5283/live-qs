namespace LiveQs.Windows.Core.Abstractions;

/// <summary>Durable record of the artifact already verified for a manifest version, so restarts never re-download.</summary>
public interface IUpdateStateStore
{
    Task<Update.VerifiedPackage> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Update.VerifiedPackage verified, CancellationToken cancellationToken = default);
}
