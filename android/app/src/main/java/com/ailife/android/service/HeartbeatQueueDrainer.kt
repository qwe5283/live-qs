package com.ailife.android.service

import android.content.Context
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.model.LifeHeartbeat
import com.ailife.android.data.queue.HeartbeatSpoolQueue
import com.ailife.android.network.ReportClient
import java.time.Duration
import java.time.Instant

class HeartbeatQueueDrainer(
    context: Context,
    private val settings: SettingsStore,
) {
    private val queue = HeartbeatSpoolQueue(context)

    fun enqueue(heartbeat: LifeHeartbeat) {
        queue.enqueue(heartbeat)
    }

    fun drainOnce(maxHeartbeats: Int = 100): Result<Int> {
        if (!settings.isReady()) return Result.success(0)

        val items = queue.readAll()
        if (items.isEmpty()) return Result.success(0)

        val freshItems = items.filter { isFresh(it.heartbeat) }
        if (freshItems.isEmpty()) {
            queue.replace(emptyList())
            return Result.success(0)
        }

        val client = ReportClient(settings.serverUrl, settings.deviceToken)
        var sent = 0
        return try {
            for (item in freshItems.take(maxHeartbeats)) {
                val upload = client.postHeartbeat(item.heartbeat)
                if (upload.isFailure) {
                    queue.replace(freshItems.drop(sent))
                    return Result.failure(upload.exceptionOrNull() ?: IllegalStateException("Heartbeat upload failed"))
                }
                sent++
            }

            queue.replace(freshItems.drop(sent))
            Result.success(sent)
        } finally {
            client.close()
        }
    }

    fun queuedCount(): Int = queue.size()

    private fun isFresh(heartbeat: LifeHeartbeat): Boolean {
        val timestamp = runCatching { Instant.parse(heartbeat.timestamp) }.getOrNull() ?: return false
        return Duration.between(timestamp, Instant.now()) <= MAX_HEARTBEAT_AGE
    }

    companion object {
        private val MAX_HEARTBEAT_AGE: Duration = Duration.ofMinutes(5)
    }
}
