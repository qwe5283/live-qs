namespace LiveQs.Windows.Core.Abstractions;

/// <summary>
/// The component release channel's client surface: fetch the component's own
/// update manifest and download its artifact with a SHA-256 verification.
/// Raw transport failures propagate to the caller, which maps them to stable
/// diagnosable codes per phase.
/// </summary>
public interface IUpdateCheckClient
{
    /// <summary>Fetches the raw manifest JSON from the component's channel URL.</summary>
    Task<string> FetchManifestAsync(string manifestUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the artifact into <paramref name="destinationDirectory"/> and
    /// returns the package path only after its SHA-256 matches the manifest
    /// digest; a mismatch throws <see cref="Update.UpdateCheckException"/> and
    /// leaves no file behind.
    /// </summary>
    Task<Update.UpdateDownloadResult> DownloadArtifactAsync(
        string downloadUrl, string destinationDirectory, string expectedSha256, CancellationToken cancellationToken = default);
}
