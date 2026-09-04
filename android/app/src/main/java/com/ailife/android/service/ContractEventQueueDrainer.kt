package com.ailife.android.service

import android.content.Context
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.queue.ContractEventSpoolQueue
import com.ailife.android.data.queue.ContractSyncFailures
import com.ailife.android.generated.EventBatchResponse
import com.ailife.android.generated.Status
import com.ailife.android.generated.VersionedEvent
import com.ailife.android.network.ReportClient
import java.io.File

/** Reconciliation counts for one drained batch: source of the sync-page counters. */
data class BatchAckCounts(
    val sent: Int,
    val accepted: Int,
    val duplicates: Int,
    val staleRevisions: Int,
    val rejected: Int,
)

/**
 * Uploads queued contract events through the versioned batch protocol with
 * per-item acknowledgements. Shared by every event domain (usage, health,
 * ...): an outbox entry is removed only once its revision is acknowledged —
 * `accepted`, `duplicate`, and `stale_revision` all confirm the revision
 * (stale means a newer revision already won server-side). Permanent
 * rejections move to the local failure queue and are never retried;
 * transport failures keep every entry for the next pass. The returned
 * [BatchAckCounts] lets the caller reconcile source record counts against
 * server results.
 */
class ContractEventQueueDrainer constructor(
    private val queue: ContractEventSpoolQueue,
    private val failures: ContractSyncFailures,
    private val upload: (List<VersionedEvent>) -> Result<EventBatchResponse>,
) {
    constructor(
        context: Context,
        settings: SettingsStore,
        queueFileName: String,
        failuresFileName: String,
    ) : this(
        queue = ContractEventSpoolQueue(File(context.filesDir, queueFileName)),
        failures = ContractSyncFailures(File(context.filesDir, failuresFileName)),
        upload = { events ->
            val client = ReportClient(settings.serverUrl, settings.deviceToken)
            try {
                client.postEventBatch(events)
            } finally {
                client.close()
            }
        },
    )

    fun enqueue(events: List<VersionedEvent>) {
        queue.enqueueAll(events)
    }

    fun drainOnce(maxEvents: Int = 500): Result<BatchAckCounts> {
        val items = queue.readAll()
        if (items.isEmpty()) return Result.success(BatchAckCounts(0, 0, 0, 0, 0))

        val batch = items.take(maxEvents)
        val response = upload(batch.map { it.event })
            .getOrElse { return Result.failure(it) }
        if (response.results.size != batch.size) {
            return Result.failure(IllegalStateException("事件批量响应数量与请求不一致"))
        }

        var accepted = 0
        var duplicates = 0
        var stale = 0
        var rejected = 0
        for ((item, acknowledgement) in batch.zip(response.results)) {
            when (acknowledgement.status) {
                Status.REJECTED -> {
                    failures.record(
                        eventId = acknowledgement.eventId,
                        revision = acknowledgement.revision,
                        errorCode = acknowledgement.error?.code ?: "rejected",
                        message = acknowledgement.error?.message ?: "The event was rejected.",
                    )
                    rejected += 1
                }
                Status.DUPLICATE -> duplicates += 1
                Status.STALE_REVISION -> stale += 1
                Status.ACCEPTED -> accepted += 1
            }
        }
        // Every acknowledgement is terminal: accepted/duplicate/stale_revision
        // confirm the revision, a rejection is preserved in the failure queue.
        // Nothing stays in the outbox, so rejections are never retried.
        queue.removeEventIds(batch.map { it.event.eventId }.toSet())
        return Result.success(BatchAckCounts(batch.size, accepted, duplicates, stale, rejected))
    }

    fun queuedCount(): Int = queue.size()
    fun failureCount(): Int = failures.size()
}
