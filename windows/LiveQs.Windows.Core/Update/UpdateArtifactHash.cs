using System.Security.Cryptography;

namespace LiveQs.Windows.Core.Update;

/// <summary>Lowercase-hex SHA-256 of a downloaded artifact, compared against the manifest digest.</summary>
public static class UpdateArtifactHash
{
    public static string Compute(Stream content)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(content)).ToLowerInvariant();
    }

    public static async Task<string> ComputeAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(content, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
