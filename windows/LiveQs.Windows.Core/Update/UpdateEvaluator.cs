namespace LiveQs.Windows.Core.Update;

/// <summary>
/// Decides whether one parsed manifest describes an update this client
/// should offer. The semantics are mirrored by scripts/release/manifest.mjs
/// and the Android UpdateEvaluator; the known-vector tests on all three pin
/// identical decisions. A manifest published for another component is never
/// an update for this client, so independent component releases can never
/// make a different client report an update.
/// </summary>
public static class UpdateEvaluator
{
    public static UpdateDecision Evaluate(string component, string currentVersion, UpdateManifest manifest)
    {
        if (!string.Equals(manifest.Component, component, StringComparison.Ordinal))
        {
            return new UpdateDecision(
                UpdateDecisionKind.Refuse,
                UpdateCodes.ManifestComponentMismatch,
                Detail: $"the manifest is for component \"{manifest.Component}\", not \"{component}\"");
        }
        if (UpdateSemver.Compare(manifest.Version, currentVersion) <= 0)
        {
            return UpdateDecision.UpToDate;
        }
        if (UpdateSemver.Compare(currentVersion, manifest.MinCompatibleVersion) < 0)
        {
            return new UpdateDecision(
                UpdateDecisionKind.Refuse,
                UpdateCodes.MinCompatibleNotMet,
                manifest.Version,
                manifest.ReleasedAt,
                manifest.DownloadUrl,
                manifest.Sha256,
                manifest.MinCompatibleVersion,
                Detail: $"running {currentVersion} predates min_compatible_version {manifest.MinCompatibleVersion}; update manually through {manifest.MinCompatibleVersion} first");
        }
        return UpdateDecision.FromManifest(manifest);
    }
}
