package com.ailife.android.usage

import com.ailife.android.identity.UuidNameIds
import java.util.UUID

/**
 * Deterministic RFC 4122 name-based (version 5) identifiers so one logical
 * usage session keeps a stable event identity across retries, process
 * restarts, and revision checkpoints. The identity is scoped by the device id
 * and a per-install GUID, so a reinstall or data wipe never collides with
 * history that was already uploaded.
 */
object UsageEventIds {
    fun forSession(deviceId: String, installGuid: String, packageName: String, startMillis: Long): UUID =
        UuidNameIds.forRecord("activity.interval", deviceId, installGuid, packageName, startMillis.toString())
}
