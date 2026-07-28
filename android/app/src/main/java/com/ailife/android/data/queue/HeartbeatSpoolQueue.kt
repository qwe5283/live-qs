package com.ailife.android.data.queue

import android.content.Context
import com.ailife.android.data.model.LifeHeartbeat
import com.ailife.android.data.model.lifeHeartbeatFromJsonObject
import com.ailife.android.data.model.toJsonObject
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.util.UUID

class HeartbeatSpoolQueue(context: Context) {
    private val queueFile = File(context.filesDir, QUEUE_FILE_NAME)

    @Synchronized
    fun enqueue(heartbeat: LifeHeartbeat) {
        queueFile.parentFile?.mkdirs()
        queueFile.appendText(
            JSONObject().apply {
                put("id", UUID.randomUUID().toString())
                put("created_at", Instant.now().toString())
                put("heartbeat", heartbeat.toJsonObject())
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
                    val json = JSONObject(line)
                    QueuedHeartbeat(
                        id = json.getString("id"),
                        createdAt = json.optString("created_at"),
                        heartbeat = lifeHeartbeatFromJsonObject(json.getJSONObject("heartbeat")),
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
                put("heartbeat", item.heartbeat.toJsonObject())
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
        val heartbeat: LifeHeartbeat,
    )

    companion object {
        private const val QUEUE_FILE_NAME = "life-heartbeats.ndjson"
        private const val MAX_QUEUED_HEARTBEATS = 2_000
    }
}
