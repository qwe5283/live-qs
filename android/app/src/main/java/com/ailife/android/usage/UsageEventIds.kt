package com.ailife.android.usage

import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

/**
 * Deterministic RFC 4122 name-based (version 5) identifiers so one logical
 * usage session keeps a stable event identity across retries, process
 * restarts, and revision checkpoints. The identity is scoped by the device id
 * and a per-install GUID, so a reinstall or data wipe never collides with
 * history that was already uploaded.
 */
object UsageEventIds {
    /** Shared LiveQs activity-interval namespace; identical to the Windows collector. */
    private val NAMESPACE = UUID.fromString("10eace7c-1a13-4a4c-af9c-5f4a1d2b3c9d")

    fun forSession(deviceId: String, installGuid: String, packageName: String, startMillis: Long): UUID =
        newUuid5(NAMESPACE, "liveqs:activity.interval:$deviceId:$installGuid:$packageName:$startMillis")

    /**
     * RFC 4122 §5.3 (SHA-1 method). The namespace consumes its big-endian raw
     * bytes; the digest is truncated to 16 bytes with the version and variant
     * bits set. The Windows collector implements the identical algorithm, and
     * the known-vector test pins both to the same output.
     */
    internal fun newUuid5(namespaceId: UUID, name: String): UUID {
        val namespaceBytes = ByteBuffer.allocate(16)
            .putLong(namespaceId.mostSignificantBits)
            .putLong(namespaceId.leastSignificantBits)
            .array()
        val digest = MessageDigest.getInstance("SHA-1")
            .digest(namespaceBytes + name.toByteArray(Charsets.UTF_8))
            .copyOf(16)
        digest[6] = ((digest[6].toInt() and 0x0F) or 0x50).toByte() // version 5
        digest[8] = ((digest[8].toInt() and 0x3F) or 0x80).toByte() // RFC 4122 variant
        val hex = digest.joinToString(separator = "") { byte -> "%02x".format(byte) }
        return UUID.fromString(
            hex.replaceFirst(
                "(\\p{XDigit}{8})(\\p{XDigit}{4})(\\p{XDigit}{4})(\\p{XDigit}{4})(\\p{XDigit}+)".toRegex(),
                "$1-$2-$3-$4-$5",
            ),
        )
    }
}
