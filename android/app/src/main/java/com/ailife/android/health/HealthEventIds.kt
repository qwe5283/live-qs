package com.ailife.android.health

import com.ailife.android.identity.UuidNameIds
import java.util.UUID

/**
 * Stable event identity for one logical Health Connect observation: UUIDv5
 * over the contract event type, the device id, the per-install GUID, and the
 * Health Connect record identifier. Replays, redeliveries, and revisions of
 * the same source record address the same logical event; a reinstall or data
 * wipe regenerates the install GUID and starts a new identity epoch that
 * never collides with uploaded history.
 */
object HealthEventIds {
    fun forRecord(eventType: String, deviceId: String, installGuid: String, recordId: String): UUID =
        UuidNameIds.forRecord(eventType, deviceId, installGuid, recordId)
}
