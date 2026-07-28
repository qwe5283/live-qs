package com.ailife.android.service

import android.content.Context
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.model.LifeEvent
import com.ailife.android.data.queue.EventSpoolQueue
import com.ailife.android.network.ReportClient

class EventQueueDrainer(
    context: Context,
    private val settings: SettingsStore,
) {
    private val queue = EventSpoolQueue(context)

    fun enqueue(events: List<LifeEvent>) {
        queue.enqueueAll(events)
    }

    fun drainOnce(maxEvents: Int = 500): Result<Int> {
        if (!settings.isReady()) return Result.success(0)

        val items = queue.readAll()
        if (items.isEmpty()) return Result.success(0)

        val batch = items.take(maxEvents)
        val client = ReportClient(settings.serverUrl, settings.deviceToken)
        return try {
            val upload = client.postEvents(batch.map { it.event })
            if (upload.isSuccess) {
                queue.replace(items.drop(batch.size))
                Result.success(batch.size)
            } else {
                Result.failure(upload.exceptionOrNull() ?: IllegalStateException("Upload failed"))
            }
        } finally {
            client.close()
        }
    }

    fun queuedCount(): Int = queue.size()
}
