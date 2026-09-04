namespace LiveQs.Windows.Core.Update;

/// <summary>Stable reason codes surfaced by the update check; diagnosable, never free-form exceptions.</summary>
public static class UpdateCodes
{
    public const string UpdateAvailable = "update_available";
    public const string ManifestVersionNotNewer = "manifest_version_not_newer";
    public const string ManifestParseFailed = "manifest_parse_failed";
    public const string ManifestComponentMismatch = "manifest_component_mismatch";
    public const string MinCompatibleNotMet = "min_compatible_not_met";
    public const string ManifestFetchFailed = "manifest_fetch_failed";
    public const string ArtifactDownloadFailed = "artifact_download_failed";
    public const string ArtifactHashMismatch = "artifact_hash_mismatch";
}

/// <summary>
/// One component release's update manifest (manifest_version 1): the stable
/// contract between the release workflow (producer) and this client
/// (consumer). Unknown fields are rejected on parse so the manifest has no
/// place to hide unvalidated content.
/// </summary>
public sealed record UpdateManifest(
    int ManifestVersion,
    string Component,
    string Version,
    DateTimeOffset ReleasedAt,
    string DownloadUrl,
    string Sha256,
    string MinCompatibleVersion);

public enum UpdateDecisionKind
{
    /// <summary>A newer applicable release exists and may be downloaded and verified.</summary>
    Available,

    /// <summary>The manifest describes this or an older version; no update is offered.</summary>
    UpToDate,

    /// <summary>The manifest must not be applied; <see cref="UpdateDecision.Code"/> says why.</summary>
    Refuse,
}

/// <summary>The update decision for one fetched manifest. Every non-available outcome carries a stable code.</summary>
public sealed record UpdateDecision(
    UpdateDecisionKind Kind,
    string Code,
    string? Version = null,
    DateTimeOffset? ReleasedAt = null,
    string? DownloadUrl = null,
    string? Sha256 = null,
    string? MinCompatibleVersion = null,
    string? Detail = null)
{
    public static UpdateDecision FromManifest(UpdateManifest manifest) => new(
        UpdateDecisionKind.Available,
        UpdateCodes.UpdateAvailable,
        manifest.Version,
        manifest.ReleasedAt,
        manifest.DownloadUrl,
        manifest.Sha256,
        manifest.MinCompatibleVersion);

    public static UpdateDecision UpToDate { get; } =
        new(UpdateDecisionKind.UpToDate, UpdateCodes.ManifestVersionNotNewer);
}

/// <summary>Components that publish update manifests; a client only ever evaluates its own component.</summary>
public static class UpdateComponents
{
    public const string Windows = "windows";
    public const string Android = "android";
}

public enum UpdateCheckState
{
    /// <summary>The update check is disabled or has not run yet.</summary>
    Idle,

    /// <summary>The component's own manifest reports no newer applicable release.</summary>
    UpToDate,

    /// <summary>A newer release was verified (artifact SHA-256 checked); manual installation is pending.</summary>
    Available,

    /// <summary>A newer release exists but this client predates its minimum compatible version; the update is refused.</summary>
    Incompatible,

    /// <summary>The check or verification failed; the update is refused until the next check succeeds.</summary>
    Failed,
}

/// <summary>The diagnosable update-check state shown in the tray and settings; never silently drives an install.</summary>
public sealed record UpdateStatus(
    UpdateCheckState State,
    bool Enabled,
    string? AvailableVersion = null,
    DateTimeOffset? ReleasedAt = null,
    string? DownloadUrl = null,
    string? VerifiedPackagePath = null,
    DateTimeOffset? LastCheckedAt = null,
    string LastErrorCode = "",
    string LastErrorMessage = "");

/// <summary>A diagnosable update-check failure carrying a stable reason code.</summary>
public sealed class UpdateCheckException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The outcome of a manifest-digest-verified artifact download.</summary>
public sealed record UpdateDownloadResult(string PackagePath, string Sha256);

/// <summary>The artifact this installation has already downloaded and hash-verified for a manifest version.</summary>
public sealed record VerifiedPackage(string? Version, string? PackagePath, string? Sha256);
