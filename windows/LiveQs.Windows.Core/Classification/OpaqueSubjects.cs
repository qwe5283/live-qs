using System.Security.Cryptography;
using System.Text;

namespace LiveQs.Windows.Core.Classification;

/// <summary>
/// Opaque identifiers for locally discovered project names. The identifier is
/// the first 128 bits of HMAC-SHA256 keyed with a device-generated secret that
/// never leaves this installation, so the same project aggregates under one
/// stable id while neither the name nor an unsalted digest of it is uploaded.
/// </summary>
public static class OpaqueSubjects
{
    private const string Prefix = "unapproved-";

    /// <summary>Derives the stable opaque subject id for one local project name.</summary>
    public static string ForProjectName(string projectName, string secretBase64)
    {
        var name = projectName.Trim();
        var key = Convert.FromBase64String(secretBase64);
        var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("project:" + name));
        var hex = Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
        return Prefix + hex;
    }

    /// <summary>Generates the per-installation classification secret (32 random bytes).</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
