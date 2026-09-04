package com.ailife.android.health

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class HealthConnectSyncStateTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun stateFile() = File(temporaryFolder.root, "health-sync-state.json")

    private fun recordState(revision: Long, fingerprint: String, endMillis: Long?) = HealthRecordState(
        recordId = "hc-record",
        eventType = "health.step.sample",
        revision = revision,
        fingerprint = fingerprint,
        endMillis = endMillis,
    )

    @Test
    fun installGuidIsDurableAndStableAcrossRestarts() {
        val first = HealthConnectSyncState(stateFile())
        val guid = first.installGuid
        assertTrue(guid.isNotBlank())

        // A new instance over the same file simulates a process restart.
        val reloaded = HealthConnectSyncState(stateFile())
        assertEquals(guid, reloaded.installGuid)
    }

    @Test
    fun aWipedStateRegeneratesTheIdentityEpoch() {
        val first = HealthConnectSyncState(stateFile())
        val guid = first.installGuid
        assertTrue(stateFile().delete())

        val recreated = HealthConnectSyncState(stateFile())
        assertNotEquals(guid, recreated.installGuid)
    }

    @Test
    fun revisionsAndFingerprintsSurviveARestart() {
        val state = HealthConnectSyncState(stateFile())
        val eventId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        state.record(eventId, recordState(revision = 2, fingerprint = "fp-1", endMillis = 1_755_700_200_000))
        state.save()

        val reloaded = HealthConnectSyncState(stateFile())
        val restored = reloaded.records.getValue(eventId)
        assertEquals(2L, restored.revision)
        assertEquals("fp-1", restored.fingerprint)
        assertEquals("health.step.sample", restored.eventType)
    }

    @Test
    fun pruningForgetsOnlyRecordsThatEndedBeforeTheHorizon() {
        val state = HealthConnectSyncState(stateFile())
        val instantEventId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
        state.record("old", recordState(1, "fp-old", endMillis = 1_000L))
        state.record("recent", recordState(1, "fp-recent", endMillis = 2_000L))
        state.record(instantEventId, recordState(1, "fp-instant", endMillis = null))

        state.pruneEndedBefore(horizonMillis = 1_500L)

        assertEquals(setOf("recent", instantEventId), state.records.keys)
    }
}
