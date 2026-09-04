package com.ailife.android.service

import android.content.Context
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.queue.UsageEventSpoolQueue
import com.ailife.android.generated.EventBatchResponse
import com.ailife.android.generated.Status
import com.ailife.android.network.ReportClient
import com.ailife.android.usage.UsageSyncFailures
import java.io.File

/**
 * Uploads queued contract events through the versioned batch protocol with
 * per-item acknowledgements. An outbox entry is removed only once its revision
 * is acknowledged: `accepted`, `duplicate`, and `stale_revision` all confirm
 * the revision (stale means a newer revision already won server-side).
 * Permanent rejections move to the local failure queue and are never retried;
 * transport failures keep every entry for the next pass.
 */
class UsageEventQueueDrainer constructor(
    private val queue: UsageEventSpoolQueue,
    private val failures: UsageSyncFailures,
    private val upload: (List<com.ailife.android.generated.ActivityIntervalEventV1>) -> Result<EventBatchResponse>,
) {
    constructor(context: Context, settings: SettingsStore) : this(
        queue = UsageEventSpoolQueue(File(context.filesDir, QUEUE_FILE_NAME)),
        failures = UsageSyncFailures(File(context.filesDir, FAILURES_FILE_NAME)),
        upload = { events ->
            val client = ReportClient(settings.serverUrl, settings.deviceToken)
            try {
                client.postEventBatch(events)
            } finally {
                client.close()
            }
        },
    )

    fun enqueue(events: List<com.ailife.android.generated.ActivityIntervalEventV1>) {
        queue.enqueueAll(events)
    }

    fun drainOnce(maxEvents: Int = 500): Result<Int> {
        val items = queue.readAll()
        if (items.isEmpty()) return Result.success(0)

        val batch = items.take(maxEvents)
        val response = upload(batch.map { it.event })
            .getOrElse { return Result.failure(it) }
        if (response.results.size != batch.size) {
            return Result.failure(IllegalStateException("事件批量响应数量与请求不一致"))
        }

        for ((item, acknowledgement) in batch.zip(response.results)) {
            if (acknowledgement.status == Status.REJECTED) {
                failures.record(
                    eventId = acknowledgement.eventId,
                    revision = acknowledgement.revision,
                    errorCode = acknowledgement.error?.code ?: "rejected",
                    message = acknowledgement.error?.message ?: "The event was rejected.",
                )
            }
        }
        // Every acknowledgement is terminal: accepted/duplicate/stale_revision
        // confirm the revision, a rejection is preserved in the failure queue.
        // Nothing stays in the outbox, so rejections are never retried.
        queue.removeEventIds(batch.map { it.event.eventId }.toSet())
        return Result.success(batch.size)
    }

    fun queuedCount(): Int = queue.size()
    fun failureCount(): Int = failures.size()

    companion object {
        private const val QUEUE_FILE_NAME = "usage-events.ndjson"
        private const val FAILURES_FILE_NAME = "usage-sync-failures.ndjson"
    }
}
