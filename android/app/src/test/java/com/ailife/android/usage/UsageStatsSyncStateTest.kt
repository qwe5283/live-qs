package com.ailife.android.usage

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UsageStatsSyncStateTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun stateFile(): File = File(temporaryFolder.root, "usage-sync-state.json")

    @Test
    fun firstLoadGeneratesAnInstallGuidThatSurvivesRestarts() {
        val first = UsageStatsSyncState(stateFile())
        val second = UsageStatsSyncState(stateFile())
        assertEquals(first.installGuid, second.installGuid)
    }

    @Test
    fun wipingTheStoreRegeneratesTheIdentityEpoch() {
        // Deliberately breaking the state file (clear data, partial wipe)
        // regenerates the GUID so replays never collide with cloud history.
        val before = UsageStatsSyncState(stateFile()).installGuid
        stateFile().delete()
        val after = UsageStatsSyncState(stateFile()).installGuid
        assertNotEquals(before, after)
    }

    @Test
    fun intervalStatesAndWatermarkSurviveAProcessRestart() {
        val state = UsageStatsSyncState(stateFile())
        val eventId = UsageEventIds.forSession("phone", state.installGuid, "com.a", 1_000).toString()
        state.record(eventId, UsageIntervalState("com.a", 1_000, null, revision = 2, isFinal = false))
        state.lastSyncEndMillis = 9_000
        state.save()

        val reloaded = UsageStatsSyncState(stateFile())
        val recorded = reloaded.intervals.getValue(eventId)
        assertEquals("com.a", recorded.packageName)
        assertNull(recorded.endMillis)
        assertEquals(2L, recorded.revision)
        assertEquals(false, recorded.isFinal)
        assertEquals(9_000L, reloaded.lastSyncEndMillis)
        assertEquals(1_000L, reloaded.oldestOpenStartMillis)
    }

    @Test
    fun pruningRemovesOnlyFinalizedSessionsBeyondTheHorizon() {
        val state = UsageStatsSyncState(stateFile())
        val oldFinal = UsageEventIds.forSession("phone", state.installGuid, "com.old", 1_000).toString()
        val freshFinal = UsageEventIds.forSession("phone", state.installGuid, "com.fresh", 2_000).toString()
        val open = UsageEventIds.forSession("phone", state.installGuid, "com.open", 3_000).toString()
        state.record(oldFinal, UsageIntervalState("com.old", 1_000, 2_000, revision = 1, isFinal = true))
        state.record(freshFinal, UsageIntervalState("com.fresh", 2_000, 3_000, revision = 1, isFinal = true))
        state.record(open, UsageIntervalState("com.open", 3_000, null, revision = 1, isFinal = false))
        state.save()

        val reloaded = UsageStatsSyncState(stateFile())
        reloaded.pruneFinalizedBefore(horizonMillis = 2_500)
        reloaded.save()

        val after = UsageStatsSyncState(stateFile())
        assertEquals(setOf(freshFinal, open), after.intervals.keys)
        // The surviving open session keeps anchoring the query window.
        assertEquals(3_000L, after.oldestOpenStartMillis)
    }
}
