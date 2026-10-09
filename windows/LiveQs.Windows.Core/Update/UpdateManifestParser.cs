using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveQs.Windows.Core.Update;

/// <summary>
/// Strict consumer-side parser for update manifests. The manifest is only
/// trusted after every field passes validation here; a parse failure is
/// always a diagnosable refusal, never a partially trusted manifest.
/// </summary>
public static partial class UpdateManifestParser
{
    private const int ManifestVersion = 1;

    private static readonly string[] _requiredFields =
    [
        "manifest_version",
        "component",
        "version",
        "released_at",
        "download_url",
        "sha256",
        "min_compatible_version",
    ];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$")]
    private static partial Regex IsoInstantPattern();

    public static bool TryParse(string json, out UpdateManifest? manifest, out IReadOnlyList<string> errors)
    {
        manifest = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            return TryParseDocument(document.RootElement, out manifest, out errors);
        }
        catch (JsonException exception)
        {
            errors = [$"the manifest is not valid JSON: {exception.Message}"];
            return false;
        }
    }

    private static bool TryParseDocument(
        JsonElement root,
        out UpdateManifest? manifest,
        out IReadOnlyList<string> errors)
    {
        manifest = null;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            errors = ["the manifest must be a JSON object"];
            return false;
        }

        var properties = root.EnumerateObject().ToArray();
        var failures = new List<string>();
        ValidateFieldNames(properties, failures);

        if (failures.Count > 0)
        {
            errors = failures;
            return false;
        }

        var fields = ReadFields(properties, failures);
        manifest = ValidateValues(fields, failures);
        errors = failures;
        return manifest is not null;
    }

    private static void ValidateFieldNames(JsonProperty[] properties, List<string> failures)
    {
        var unknown = properties
            .Select(property => property.Name)
            .Where(name => !_requiredFields.Contains(name))
            .ToArray();
        var missing = _requiredFields
            .Where(field => !properties.Any(property => string.Equals(property.Name, field, StringComparison.Ordinal)))
            .ToArray();

        if (unknown.Length > 0)
        {
            failures.Add($"unknown fields: {string.Join(", ", unknown)}");
        }

        if (missing.Length > 0)
        {
            failures.Add($"missing fields: {string.Join(", ", missing)}");
        }
    }

    private static ManifestFields ReadFields(JsonProperty[] properties, List<string> failures)
    {
        var manifestVersion = 0;
        var component = "";
        var version = "";
        var releasedAt = "";
        var downloadUrl = "";
        var sha256 = "";
        var minCompatible = "";
        foreach (var property in properties)
        {
            switch (property.Name)
            {
                case "manifest_version":
                    if (property.Value.ValueKind is JsonValueKind.Number && property.Value.TryGetInt32(out var parsed))
                    {
                        manifestVersion = parsed;
                    }
                    else
                    {
                        failures.Add("manifest_version must be an integer");
                    }

                    break;
                case "component":
                    component = ReadString(property.Value, failures, "component");
                    break;
                case "version":
                    version = ReadString(property.Value, failures, "version");
                    break;
                case "released_at":
                    releasedAt = ReadString(property.Value, failures, "released_at");
                    break;
                case "download_url":
                    downloadUrl = ReadString(property.Value, failures, "download_url");
                    break;
                case "sha256":
                    sha256 = ReadString(property.Value, failures, "sha256");
                    break;
                case "min_compatible_version":
                    minCompatible = ReadString(property.Value, failures, "min_compatible_version");
                    break;
            }
        }

        return new ManifestFields(
            manifestVersion, component, version, releasedAt, downloadUrl, sha256, minCompatible);
    }

    private static UpdateManifest? ValidateValues(ManifestFields fields, List<string> failures)
    {
        if (fields.ManifestVersion != ManifestVersion)
        {
            failures.Add($"manifest_version must be {ManifestVersion}");
        }

        if (!IsLowercaseIdentifier(fields.Component))
        {
            failures.Add("component must be a lowercase [a-z][a-z0-9-]* identifier");
        }

        if (!UpdateSemver.IsCoreSemver(fields.Version))
        {
            failures.Add("version must be core semver X.Y.Z");
        }

        if (!UpdateSemver.IsCoreSemver(fields.MinCompatibleVersion))
        {
            failures.Add("min_compatible_version must be core semver X.Y.Z");
        }

        if (!Sha256Pattern().IsMatch(fields.Sha256))
        {
            failures.Add("sha256 must be a lowercase 64-hex digest");
        }

        if (!Uri.TryCreate(fields.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            failures.Add("download_url must be an absolute http(s) URL");
        }
        if (!TryParseUtcInstant(fields.ReleasedAt, out var released))
        {
            failures.Add("released_at must be a UTC ISO-8601 instant (YYYY-MM-DDTHH:MM:SSZ)");
        }

        if (failures.Count == 0 && UpdateSemver.Compare(fields.MinCompatibleVersion, fields.Version) > 0)
        {
            failures.Add("min_compatible_version must not exceed version");
        }
        if (failures.Count > 0)
        {
            return null;
        }

        return new UpdateManifest(
            fields.ManifestVersion,
            fields.Component,
            fields.Version,
            released,
            fields.DownloadUrl,
            fields.Sha256,
            fields.MinCompatibleVersion);
    }

    private static string ReadString(JsonElement value, List<string> failures, string field)
    {
        if (value.ValueKind is not JsonValueKind.String)
        {
            failures.Add($"{field} must be a string");
            return "";
        }
        return value.GetString() ?? "";
    }

    private static bool IsLowercaseIdentifier(string value) =>
        value.Length > 0 && value[0] >= 'a' && value[0] <= 'z' &&
        value.All(character =>
            (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-');

    private static bool TryParseUtcInstant(string value, out DateTimeOffset instant)
    {
        instant = default;
        if (!IsoInstantPattern().IsMatch(value))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out instant) && instant.Offset == TimeSpan.Zero;
    }

    private sealed record ManifestFields(
        int ManifestVersion,
        string Component,
        string Version,
        string ReleasedAt,
        string DownloadUrl,
        string Sha256,
        string MinCompatibleVersion);
}
