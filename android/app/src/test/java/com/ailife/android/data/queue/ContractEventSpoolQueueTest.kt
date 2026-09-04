package com.ailife.android.data.queue

import com.ailife.android.generated.Device
import com.ailife.android.generated.EventType
import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.Payload
import com.ailife.android.generated.Platform
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.Provenance
import com.ailife.android.generated.Source
import com.ailife.android.generated.SourceKind
import com.ailife.android.generated.VersionedEvent
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class ContractEventSpoolQueueTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun spool(name: String) = ContractEventSpoolQueue(File(temporaryFolder.root, name))

    private fun event(eventId: String, revision: Long, startAt: String = "2026-08-20T01:30:00.000Z") = VersionedEvent(
        eventType = EventType.HEALTH_STEP_SAMPLE,
        payload = Payload(count = com.ailife.android.generated.Count(unit = com.ailife.android.generated.StepUnit.STEPS, value = 100), dataOrigin = "com.origin"),
        privacyLevel = PrivacyLevel.SENSITIVE,
        schemaVersion = 1,
        source = Source(kind = SourceKind.ANDROID_HEALTHCONNECT, recordId = "record-$eventId"),
        captureOffsetMinutes = 480,
        captureTimezone = "Asia/Shanghai",
        device = Device(id = "phone", platform = Platform.ANDROID),
        endAt = "2026-08-20T02:00:00.000Z",
        eventId = eventId,
        finalizationState = FinalizationState.FINAL,
        invalidated = false,
        ownerId = "local",
        provenance = Provenance(collectorVersion = "0.2.0", observedAt = "2026-08-20T02:05:00.000Z"),
        revision = revision,
        startAt = startAt,
    )

    @Test
    fun entriesSurviveProcessRestart() {
        val queue = spool("spool.ndjson")
        queue.enqueueAll(listOf(event("11111111-1111-4111-8111-111111111111", 1)))

        // A new instance over the same file simulates a process restart.
        val reloaded = ContractEventSpoolQueue(File(temporaryFolder.root, "spool.ndjson"))
        assertEquals(1, reloaded.size())
        assertEquals("11111111-1111-4111-8111-111111111111", reloaded.readAll().single().event.eventId)
    }

    @Test
    fun enqueueUpsertsByEventKeepingOnlyTheNewestPendingRevision() {
        val queue = spool("spool.ndjson")
        val id = "22222222-2222-4222-8222-222222222222"
        queue.enqueueAll(listOf(event(id, 1)))
        queue.enqueueAll(listOf(event(id, 2)))

        assertEquals(1, queue.size())
        assertEquals(2L, queue.readAll().single().event.revision)

        // A stale revision arriving late never replaces the pending newer one.
        queue.enqueueAll(listOf(event(id, 1)))
        assertEquals(2L, queue.readAll().single().event.revision)
    }

    @Test
    fun removeEventIdsDeletesOnlyTheAcknowledgedEntries() {
        val queue = spool("spool.ndjson")
        val kept = "33333333-3333-4333-8333-333333333301"
        val removed = "33333333-3333-4333-8333-333333333302"
        queue.enqueueAll(listOf(event(kept, 1), event(removed, 1)))

        queue.removeEventIds(setOf(removed))

        assertEquals(listOf(kept), queue.readAll().map { it.event.eventId })
    }

    @Test
    fun corruptedLinesAreSkippedInsteadOfBreakingTheQueue() {
        val file = File(temporaryFolder.root, "spool.ndjson")
        val queue = ContractEventSpoolQueue(file)
        queue.enqueueAll(listOf(event("44444444-4444-4444-8444-444444444444", 1)))
        file.appendText("{ not json\n")

        assertEquals(1, queue.size())
        assertTrue(queue.readAll().first().event.eventId.endsWith("444444444444"))
    }
}
