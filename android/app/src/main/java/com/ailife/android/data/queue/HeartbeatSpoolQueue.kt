package com.ailife.android.data.queue

import android.content.Context
import com.ailife.android.generated.HeartbeatRequest
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.util.UUID

/**
 * Durable spool for current-state heartbeats captured while the collector is
 * alive. Heartbeats are ephemeral projections, so the spool only bridges short
 * outages: the drainer drops entries older than its freshness window instead
 * of replaying stale states as if they were current.
 */
class HeartbeatSpoolQueue(context: Context) {
    private val queueFile = File(context.filesDir, QUEUE_FILE_NAME)
    private val json = Json

    @Synchronized
    fun enqueue(heartbeat: HeartbeatRequest) {
        queueFile.parentFile?.mkdirs()
        queueFile.appendText(
            JSONObject().apply {
                put("id", UUID.randomUUID().toString())
                put("created_at", Instant.now().toString())
                put("heartbeat", json.encodeToString(heartbeat))
            }.toString() + "\n",
        )
        compactIfNeeded()
    }

    @Synchronized
    fun readAll(): List<QueuedHeartbeat> {
        if (!queueFile.exists()) return emptyList()

        return queueFile.readLines()
            .asSequence()
            .filter { it.isNotBlank() }
            .mapNotNull { line ->
                runCatching {
                    val envelope = JSONObject(line)
                    QueuedHeartbeat(
                        id = envelope.getString("id"),
                        createdAt = envelope.optString("created_at"),
                        heartbeat = json.decodeFromString<HeartbeatRequest>(
                            envelope.getJSONObject("heartbeat").toString(),
                        ),
                    )
                }.getOrNull()
            }
            .toList()
    }

    @Synchronized
    fun replace(items: List<QueuedHeartbeat>) {
        if (items.isEmpty()) {
            if (queueFile.exists()) queueFile.delete()
            return
        }

        val tempFile = File("${queueFile.absolutePath}.tmp")
        tempFile.writeText(items.joinToString(separator = "\n", postfix = "\n") { item ->
            JSONObject().apply {
                put("id", item.id)
                put("created_at", item.createdAt)
                put("heartbeat", json.encodeToString(item.heartbeat))
            }.toString()
        })
        tempFile.copyTo(queueFile, overwrite = true)
        tempFile.delete()
    }

    @Synchronized
    fun size(): Int = readAll().size

    private fun compactIfNeeded() {
        val items = readAll()
        if (items.size <= MAX_QUEUED_HEARTBEATS) return
        replace(items.takeLast(MAX_QUEUED_HEARTBEATS))
    }

    data class QueuedHeartbeat(
        val id: String,
        val createdAt: String,
        val heartbeat: HeartbeatRequest,
    )

    companion object {
        private const val QUEUE_FILE_NAME = "life-heartbeats.ndjson"
        private const val MAX_QUEUED_HEARTBEATS = 2_000
    }
}
