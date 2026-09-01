using System.Security.Cryptography;
using System.Text;

namespace LiveQs.Windows.Infrastructure.Sync;

/// <summary>
/// Deterministic RFC 4122 name-based (version 5) identifiers so one logical
/// activity segment keeps a stable event identity across retries, process
/// restarts, and revision checkpoints. The identity is scoped by the device id
/// and the local database installation id, so a wiped database never collides
/// with history that was already uploaded.
/// </summary>
public static class EventIds
{
    private static readonly Guid Namespace = new("10eace7c-1a13-4a4c-af9c-5f4a1d2b3c9d");

    public static Guid ForSegment(string deviceId, string installId, long segmentId) =>
        NewUuid5(Namespace, $"liveqs:activity.interval:{deviceId}:{installId}:{segmentId}");

    private static Guid NewUuid5(Guid namespaceId, string name)
    {
        // The "N" hex form is the RFC 4122 big-endian byte order, which is what the hash must consume.
        var namespaceBytes = Convert.FromHexString(namespaceId.ToString("N"));
        var hash = SHA1.HashData(namespaceBytes.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        Array.Resize(ref hash, 16);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(Convert.ToHexString(hash));
    }
}
