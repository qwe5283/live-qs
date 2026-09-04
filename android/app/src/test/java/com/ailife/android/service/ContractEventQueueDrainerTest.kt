package com.ailife.android.service

import com.ailife.android.data.queue.ContractEventSpoolQueue
import com.ailife.android.data.queue.ContractSyncFailures
import com.ailife.android.generated.Error
import com.ailife.android.generated.EventAcknowledgement
import com.ailife.android.generated.EventBatchResponse
import com.ailife.android.generated.EventAcknowledgementStatus
import com.ailife.android.health.HealthEventIds
import com.ailife.android.health.HealthConnectEventPlanner
import com.ailife.android.health.HealthSleepSample
import java.io.File
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class ContractEventQueueDrainerTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun spool() = ContractEventSpoolQueue(File(temporaryFolder.root, "spool.ndjson"))
    private fun failures() = ContractSyncFailures(File(temporaryFolder.root, "failures.ndjson"))

    /** One contract-valid event per pending outbox entry, built with the production health planner. */
    private fun plannedEvent(recordId: String) =
        HealthConnectEventPlanner.plan(
            samples = listOf(HealthSleepSample(recordId, "com.urbandroid.sleep", 1_754_043_000_000, 1_754_046_200_000)),
            state = emptyMap(),
            deviceId = "phone",
            ownerId = "local",
            installGuid = "guid",
            nowMillis = 1_754_046_300_000,
            collectorVersion = "0.2.0",
            zone = ZoneId.of("Asia/Shanghai"),
        ).events.single()

    private fun acknowledgement(status: EventAcknowledgementStatus, eventId: String, code: String? = null) = EventAcknowledgement(
        error = code?.let { Error(code = it, message = "explained") },
        eventId = eventId,
        revision = 1,
        status = status,
    )

    @Test
    fun acceptedDuplicateAndStaleRevisionsAcknowledgeTheOutboxEntry() {
        for (status in listOf(EventAcknowledgementStatus.ACCEPTED, EventAcknowledgementStatus.DUPLICATE, EventAcknowledgementStatus.STALE_REVISION)) {
            val spool = spool()
            val failures = failures()
            val event = plannedEvent("hc-record-$status")
            spool.enqueueAll(listOf(event))

            val result = ContractEventQueueDrainer(spool, failures) {
                Result.success(EventBatchResponse(listOf(acknowledgement(status, event.eventId))))
            }.drainOnce()

            assertTrue("$status should drain successfully", result.isSuccess)
            val counts = result.getOrThrow()
            assertEquals("$status should empty the outbox", 0, spool.size())
            assertEquals("$status is not a failure", 0, failures.size())
            assertEquals("reconciliation: sent", 1, counts.sent)
            assertEquals("reconciliation: $status counted", 1, when (status) {
                EventAcknowledgementStatus.ACCEPTED -> counts.accepted
                EventAcknowledgementStatus.DUPLICATE -> counts.duplicates
                EventAcknowledgementStatus.STALE_REVISION -> counts.staleRevisions
                EventAcknowledgementStatus.REJECTED -> counts.rejected
            })
        }
    }

    @Test
    fun permanentRejectionMovesTheEventToTheVisibleFailureQueueAndNeverRetries() {
        val spool = spool()
        val failures = failures()
        val event = plannedEvent("hc-record-rejected")
        spool.enqueueAll(listOf(event))
        var uploadAttempts = 0

        val outcome = Result.success(
            EventBatchResponse(listOf(acknowledgement(EventAcknowledgementStatus.REJECTED, event.eventId, code = "privacy_ceiling_exceeded"))),
        )
        val result = ContractEventQueueDrainer(spool, failures) {
            uploadAttempts += 1
            outcome
        }.drainOnce()

        assertTrue(result.isSuccess)
        assertEquals(0, spool.size()) // removed from the outbox: never retried
        assertEquals(1, uploadAttempts)
        val counts = result.getOrThrow()
        assertEquals(1, counts.rejected)
        val recorded = failures.readAll().single()
        assertEquals(event.eventId, recorded.eventId)
        assertEquals("privacy_ceiling_exceeded", recorded.errorCode)

        // A second drain has nothing left to upload.
        ContractEventQueueDrainer(spool, failures) {
            uploadAttempts += 1
            Result.success(EventBatchResponse(emptyList()))
        }.drainOnce()
        assertEquals(1, uploadAttempts)
    }

    @Test
    fun transportFailuresKeepEveryEntryForTheNextPass() {
        val spool = spool()
        val failures = failures()
        spool.enqueueAll(listOf(plannedEvent("hc-record-transport")))

        val result = ContractEventQueueDrainer(spool, failures) {
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
        spool.enqueueAll(listOf(plannedEvent("hc-record-shape")))

        val result = ContractEventQueueDrainer(spool, failures) {
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
        val accepted = plannedEvent("hc-record-accepted")
        val rejected = plannedEvent("hc-record-invalid")
        spool.enqueueAll(listOf(accepted, rejected))

        val result = ContractEventQueueDrainer(spool, failures) {
            Result.success(
                EventBatchResponse(
                    listOf(
                        acknowledgement(EventAcknowledgementStatus.ACCEPTED, accepted.eventId),
                        acknowledgement(EventAcknowledgementStatus.REJECTED, rejected.eventId, code = "invalid_event"),
                    ),
                ),
            )
        }.drainOnce()

        assertTrue(result.isSuccess)
        assertEquals(0, spool.size())
        assertEquals(listOf(rejected.eventId), failures.readAll().map { it.eventId })
        val counts = result.getOrThrow()
        assertEquals(2, counts.sent)
        assertEquals(1, counts.accepted)
        assertEquals(1, counts.rejected)
        // Reconciliation identity: the source record id maps one-to-one onto the event id.
        assertEquals(
            HealthEventIds.forRecord(
                "health.sleep.session",
                "phone",
                "guid",
                "hc-record-invalid",
            ).toString(),
            rejected.eventId,
        )
    }
}
