package com.ailife.android.service

import com.ailife.android.data.queue.UsageEventSpoolQueue
import com.ailife.android.generated.Error
import com.ailife.android.generated.EventAcknowledgement
import com.ailife.android.generated.EventBatchResponse
import com.ailife.android.generated.Status
import com.ailife.android.usage.UsageInterval
import com.ailife.android.usage.UsageStatsEventPlanner
import com.ailife.android.usage.UsageStatsSyncStateView
import com.ailife.android.usage.UsageSyncFailures
import java.io.File
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UsageEventQueueDrainerTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun spool() = UsageEventSpoolQueue(File(temporaryFolder.root, "spool.ndjson"))
    private fun failures() = UsageSyncFailures(File(temporaryFolder.root, "failures.ndjson"))

    /** One contract-valid event per pending outbox entry, built with the production planner. */
    private fun plannedEvent(packageName: String, startMillis: Long, endMillis: Long) =
        UsageStatsEventPlanner.plan(
            intervals = listOf(UsageInterval(packageName, startMillis, endMillis)),
            state = UsageStatsSyncStateView(installGuid = "guid", intervals = emptyMap()),
            deviceId = "phone",
            ownerId = "local",
            nowMillis = endMillis,
            collectorVersion = "0.1.0",
            zone = ZoneId.of("Asia/Shanghai"),
        ).events.single()

    private fun acknowledgement(status: Status, eventId: String, code: String? = null) = EventAcknowledgement(
        error = code?.let { Error(code = it, message = "explained") },
        eventId = eventId,
        revision = 1,
        status = status,
    )

    @Test
    fun acceptedDuplicateAndStaleRevisionsAcknowledgeTheOutboxEntry() {
        for (status in listOf(Status.ACCEPTED, Status.DUPLICATE, Status.STALE_REVISION)) {
            val spool = spool()
            val failures = failures()
            val event = plannedEvent("tv.danmaku.bili", 1_754_043_000_000, 1_754_043_060_000)
            spool.enqueueAll(listOf(event))

            val result = UsageEventQueueDrainer(spool, failures) {
                Result.success(EventBatchResponse(listOf(acknowledgement(status, event.eventId))))
            }.drainOnce()

            assertTrue("$status should drain successfully", result.isSuccess)
            assertEquals("$status should empty the outbox", 0, spool.size())
            assertEquals("$status is not a failure", 0, failures.size())
        }
    }

    @Test
    fun permanentRejectionMovesTheEventToTheVisibleFailureQueueAndNeverRetries() {
        val spool = spool()
        val failures = failures()
        val event = plannedEvent("tv.danmaku.bili", 1_754_043_000_000, 1_754_043_060_000)
        spool.enqueueAll(listOf(event))
        var uploadAttempts = 0

        val outcome = Result.success(
            EventBatchResponse(listOf(acknowledgement(Status.REJECTED, event.eventId, code = "privacy_ceiling_exceeded"))),
        )
        val result = UsageEventQueueDrainer(spool, failures) {
            uploadAttempts += 1
            outcome
        }.drainOnce()

        assertTrue(result.isSuccess)
        assertEquals(0, spool.size()) // removed from the outbox: never retried
        assertEquals(1, uploadAttempts)
        val recorded = failures.readAll().single()
        assertEquals(event.eventId, recorded.eventId)
        assertEquals("privacy_ceiling_exceeded", recorded.errorCode)

        // A second drain has nothing left to upload.
        UsageEventQueueDrainer(spool, failures) {
            uploadAttempts += 1
            Result.success(EventBatchResponse(emptyList()))
        }.drainOnce()
        assertEquals(1, uploadAttempts)
    }

    @Test
    fun transportFailuresKeepEveryEntryForTheNextPass() {
        val spool = spool()
        val failures = failures()
        spool.enqueueAll(listOf(plannedEvent("tv.danmaku.bili", 1_754_043_000_000, 1_754_043_060_000)))

        val result = UsageEventQueueDrainer(spool, failures) {
            Result.failure(IllegalStateException("network down"))
        }.drainOnce()

        assertTrue(result.isFailure)
        assertEquals(1, spool.size())
        assertEquals(0, failures.size())
    }

    @Test
    fun aResponseShapeMismatchKeepsTheOutboxIntact() {
        val spool = spool()
        val failures = failures()
        spool.enqueueAll(listOf(plannedEvent("tv.danmaku.bili", 1_754_043_000_000, 1_754_043_060_000)))

        val result = UsageEventQueueDrainer(spool, failures) {
            Result.success(EventBatchResponse(results = emptyList()))
        }.drainOnce()

        assertTrue(result.isFailure)
        assertEquals(1, spool.size())
        assertEquals(0, failures.size())
    }

    @Test
    fun mixedOutcomesResolveEachItemAccordingToItsOwnAcknowledgement() {
        val spool = spool()
        val failures = failures()
        val accepted = plannedEvent("tv.danmaku.bili", 1_754_043_000_000, 1_754_043_060_000)
        val rejected = plannedEvent("com.second", 1_754_043_100_000, 1_754_043_120_000)
        spool.enqueueAll(listOf(accepted, rejected))

        val result = UsageEventQueueDrainer(spool, failures) {
            Result.success(
                EventBatchResponse(
                    listOf(
                        acknowledgement(Status.ACCEPTED, accepted.eventId),
                        acknowledgement(Status.REJECTED, rejected.eventId, code = "invalid_event"),
                    ),
                ),
            )
        }.drainOnce()

        assertTrue(result.isSuccess)
        assertEquals(0, spool.size())
        assertEquals(listOf(rejected.eventId), failures.readAll().map { it.eventId })
    }
}
