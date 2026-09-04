package com.ailife.android.identity

import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

/**
 * Deterministic RFC 4122 name-based (version 5) identifiers shared by every
 * event domain (activity intervals, Health Connect observations, ...). One
 * logical fact keeps a stable event identity across retries, process
 * restarts, and revision checkpoints; the identity is scoped by the device id
 * and a per-install GUID, so a reinstall or data wipe never collides with
 * history that was already uploaded.
 */
object UuidNameIds {
    /** Shared LiveQs event identity namespace; identical to the Windows collector. */
    val LIVEQS_NAMESPACE: UUID = UUID.fromString("10eace7c-1a13-4a4c-af9c-5f4a1d2b3c9d")

    /**
     * Builds one identity from the event domain (the contract event type), the
     * device id, the per-install GUID, and the domain-specific record parts.
     */
    fun forRecord(
        eventType: String,
        deviceId: String,
        installGuid: String,
        vararg recordParts: String,
    ): UUID = newUuid5(
        LIVEQS_NAMESPACE,
        listOf("liveqs:$eventType", deviceId, installGuid, *recordParts).joinToString(":"),
    )

    /**
     * RFC 4122 §5.3 (SHA-1 method). The namespace consumes its big-endian raw
     * bytes; the digest is truncated to 16 bytes with the version and variant
     * bits set. The Windows collector implements the identical algorithm, and
     * the known-vector test pins both to the same output.
     */
    fun newUuid5(namespaceId: UUID, name: String): UUID {
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
