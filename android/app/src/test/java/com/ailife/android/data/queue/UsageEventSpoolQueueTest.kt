package com.ailife.android.data.queue

import com.ailife.android.generated.ActivityIntervalEventV1
import com.ailife.android.generated.Device
import com.ailife.android.generated.Duration
import com.ailife.android.generated.DurationUnit
import com.ailife.android.generated.EventType
import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.Payload
import com.ailife.android.generated.Platform
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.Provenance
import com.ailife.android.generated.Source
import com.ailife.android.generated.SourceKind
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UsageEventSpoolQueueTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun queue(): UsageEventSpoolQueue = UsageEventSpoolQueue(File(temporaryFolder.root, "usage-events.ndjson"))

    private fun event(eventId: String, revision: Long, durationMs: Long = 60_000): ActivityIntervalEventV1 =
        ActivityIntervalEventV1(
            eventType = EventType.ACTIVITY_INTERVAL,
            payload = Payload(
                applicationId = "tv.danmaku.bili",
                duration = Duration(value = durationMs, unit = DurationUnit.MS),
                isAfk = false,
            ),
            privacyLevel = PrivacyLevel.NORMAL,
            schemaVersion = 1,
            source = Source(kind = SourceKind.ANDROID_USAGESTATS, recordId = "usage-session-x"),
            captureOffsetMinutes = 480,
            captureTimezone = "Asia/Shanghai",
            device = Device(id = "phone", platform = Platform.ANDROID),
            endAt = "2026-08-01T13:31:00.000Z",
            eventId = eventId,
            finalizationState = FinalizationState.CHECKPOINT,
            invalidated = false,
            ownerId = "local",
            provenance = Provenance(collectorVersion = "0.1.0", observedAt = "2026-08-01T13:31:01.000Z"),
            revision = revision,
            startAt = "2026-08-01T13:30:00.000Z",
        )

    @Test
    fun eventsSurviveAProcessRestart() {
        queue().enqueueAll(listOf(event("11111111-1111-4111-8111-111111111111", 1)))
        val reloaded = queue().readAll().single().event
        assertEquals("11111111-1111-4111-8111-111111111111", reloaded.eventId)
        assertEquals(1L, reloaded.revision)
        assertEquals("tv.danmaku.bili", reloaded.payload.applicationId)
        assertEquals(SourceKind.ANDROID_USAGESTATS, reloaded.source.kind)
    }

    @Test
    fun enqueueingANewerRevisionReplacesThePendingCopyOfTheSameLogicalEvent() {
        val eventId = "22222222-2222-4222-8222-222222222222"
        val spool = queue()
        spool.enqueueAll(listOf(event(eventId, 1, durationMs = 60_000)))
        spool.enqueueAll(listOf(event(eventId, 2, durationMs = 120_000)))
        spool.enqueueAll(listOf(event(eventId, 1, durationMs = 60_000))) // stale redelivery ignored

        val pending = spool.readAll()
        assertEquals(1, pending.size)
        assertEquals(2L, pending.single().event.revision)
        assertEquals(120_000L, pending.single().event.payload.duration.value)
    }

    @Test
    fun acknowledgementRemovesOnlyTheAcknowledgedLogicalEvents() {
        val spool = queue()
        spool.enqueueAll(
            listOf(
                event("33333333-3333-4333-8333-333333333331", 1),
                event("33333333-3333-4333-8333-333333333332", 1),
            ),
        )
        spool.removeEventIds(setOf("33333333-3333-4333-8333-333333333331"))
        assertEquals(
            listOf("33333333-3333-4333-8333-333333333332"),
            spool.readAll().map { it.event.eventId },
        )
    }

    @Test
    fun corruptedLinesAreSkippedInsteadOfBlockingTheQueue() {
        val file = File(temporaryFolder.root, "usage-events.ndjson")
        file.parentFile?.mkdirs()
        file.writeText("{broken json\n")
        val spool = UsageEventSpoolQueue(file)
        spool.enqueueAll(listOf(event("44444444-4444-4444-8444-444444444444", 1)))
        assertEquals(1, spool.size())
        assertTrue(spool.readAll().single().event.eventId == "44444444-4444-4444-8444-444444444444")
    }
}
