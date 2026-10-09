using System.Text;
using System.Text.Json.Nodes;
using LiveQs.Windows.Core.Update;

namespace LiveQs.Windows.Tests;

public sealed class UpdateTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string ValidManifestJson = """
        {
          "manifest_version": 1,
          "component": "windows",
          "version": "0.2.0",
          "released_at": "2026-09-04T08:00:00Z",
          "download_url": "https://github.com/qwe5283/live-qs/releases/download/windows%2Fv0.2.0/LiveQs.Windows-0.2.0-win-x64.zip",
          "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
          "min_compatible_version": "0.1.0"
        }
        """;

    [Theory]
    [InlineData("0.1.0", "0.2.0", -1)]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("1.2.3", "1.2.10", -1)]
    [InlineData("10.0.0", "9.99.99", 1)]
    public void Compare_OrdersCoreSemverNumerically(string left, string right, int expected)
    {
        Assert.Equal(expected, UpdateSemver.Compare(left, right));
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("01.2.0")]
    [InlineData("x.y.z")]
    [InlineData("")]
    public void Compare_RejectsNonCoreSemver(string invalid)
    {
        Assert.Throws<FormatException>(() => UpdateSemver.Compare(invalid, "0.1.0"));
    }

    [Fact]
    public void TryParse_AcceptsAValidManifest()
    {
        var parsed = UpdateManifestParser.TryParse(ValidManifestJson, out var manifest, out var errors);

        Assert.True(parsed, string.Join("; ", errors));
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal("windows", manifest.Component);
        Assert.Equal("0.2.0", manifest.Version);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero), manifest.ReleasedAt);
        Assert.StartsWith("https://github.com/", manifest.DownloadUrl);
        Assert.Equal(EmptySha256, manifest.Sha256);
        Assert.Equal("0.1.0", manifest.MinCompatibleVersion);
    }

    public static TheoryData<string> InvalidManifests => new()
    {
        InvalidManifest("manifest_version", "2"),
        InvalidManifest("manifest_version", "\"1\""),
        InvalidManifest("version", "\"0.2\""),
        InvalidManifest("released_at", "\"yesterday\""),
        InvalidManifest("component", "\"Windows\""),
        InvalidManifest("download_url", "\"ftp://a/b\""),
        InvalidManifest("sha256", "\"NOT-A-HASH\""),
        InvalidManifest("min_compatible_version", "\"0.3.0\""),
        InvalidManifest("notes", "\"extra\""),
        InvalidManifest("min_compatible_version", null),
        "not json at all",
        "42",
    };

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void TryParse_RejectsInvalidManifestsWithDiagnosableErrors(string json)
    {
        var parsed = UpdateManifestParser.TryParse(json, out var manifest, out var errors);

        Assert.False(parsed);
        Assert.Null(manifest);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Evaluate_OffersAStrictlyNewerApplicableRelease()
    {
        var manifest = Parse(ValidManifestJson);

        var decision = UpdateEvaluator.Evaluate("windows", "0.1.0", manifest);

        Assert.Equal(UpdateDecisionKind.Available, decision.Kind);
        Assert.Equal(UpdateCodes.UpdateAvailable, decision.Code);
        Assert.Equal("0.2.0", decision.Version);
        Assert.Equal(EmptySha256, decision.Sha256);
        Assert.NotNull(decision.DownloadUrl);
    }

    [Theory]
    [InlineData("0.3.1", "0.2.0", "0.1.0")]
    [InlineData("0.2.0", "0.2.0", "0.2.0")]
    [InlineData("0.1.0", "0.0.9", "0.0.9")]
    public void Evaluate_NeverOffersADowngradeOrReinstall(
        string currentVersion,
        string manifestVersion,
        string minCompatible)
    {
        var manifest = Parse(ManifestJson("windows", manifestVersion, minCompatible));

        var decision = UpdateEvaluator.Evaluate("windows", currentVersion, manifest);

        Assert.Equal(UpdateDecisionKind.UpToDate, decision.Kind);
        Assert.Equal(UpdateCodes.ManifestVersionNotNewer, decision.Code);
    }

    [Fact]
    public void Evaluate_RefusesAManifestPublishedForAnotherComponent()
    {
        // Isolation property: another component's release channel must never
        // look like an update for this client, however new its version is.
        var manifest = Parse(ManifestJson("android", "99.0.0", "0.1.0"));

        var decision = UpdateEvaluator.Evaluate("windows", "0.1.0", manifest);

        Assert.Equal(UpdateDecisionKind.Refuse, decision.Kind);
        Assert.Equal(UpdateCodes.ManifestComponentMismatch, decision.Code);
    }

    [Fact]
    public void Evaluate_RefusesWhenTheRunningClientPredatesTheMinimumCompatibleVersion()
    {
        var manifest = Parse(ValidManifestJson);

        var decision = UpdateEvaluator.Evaluate("windows", "0.0.9", manifest);

        Assert.Equal(UpdateDecisionKind.Refuse, decision.Kind);
        Assert.Equal(UpdateCodes.MinCompatibleNotMet, decision.Code);
    }

    [Fact]
    public void Evaluate_AcceptsAClientExactlyAtTheMinimumCompatibleVersion()
    {
        var manifest = Parse(ValidManifestJson);

        var decision = UpdateEvaluator.Evaluate("windows", "0.1.0", manifest);

        Assert.Equal(UpdateDecisionKind.Available, decision.Kind);
    }

    [Fact]
    public void Compute_HashesAnEmptyStreamLikeTheKnownNistVector()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        Assert.Equal(EmptySha256, UpdateArtifactHash.Compute(stream));
    }

    [Fact]
    public void Compute_ProducesLowercaseHex()
    {
        var bytes = Encoding.UTF8.GetBytes("liveqs");
        using var stream = new MemoryStream(bytes);

        var hash = UpdateArtifactHash.Compute(stream);

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    private static string InvalidManifest(string field, string? valueJson)
    {
        var manifest = JsonNode.Parse(ValidManifestJson)!.AsObject();
        if (valueJson is null)
        {
            manifest.Remove(field);
        }
        else
        {
            manifest[field] = JsonNode.Parse(valueJson);
        }
        return manifest.ToJsonString();
    }

    private static UpdateManifest Parse(string json)
    {
        Assert.True(UpdateManifestParser.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));
        return manifest!;
    }

    private static string ManifestJson(string component, string version, string minCompatible) => $$"""
        {
          "manifest_version": 1,
          "component": "{{component}}",
          "version": "{{version}}",
          "released_at": "2026-09-04T08:00:00Z",
          "download_url": "https://github.com/qwe5283/live-qs/releases/download/{{component}}%2Fv{{version}}/artifact",
          "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
          "min_compatible_version": "{{minCompatible}}"
        }
        """;
}
