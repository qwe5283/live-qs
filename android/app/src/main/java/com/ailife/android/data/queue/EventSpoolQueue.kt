package com.ailife.android.data.queue

import android.content.Context
import com.ailife.android.data.model.LifeEvent
import com.ailife.android.data.model.lifeEventFromJsonObject
import com.ailife.android.data.model.toJsonObject
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.util.UUID

class EventSpoolQueue(context: Context) {
    private val queueFile = File(context.filesDir, QUEUE_FILE_NAME)

    @Synchronized
    fun enqueueAll(events: List<LifeEvent>) {
        if (events.isEmpty()) return

        queueFile.parentFile?.mkdirs()
        queueFile.appendText(events.joinToString(separator = "\n", postfix = "\n") { event ->
            JSONObject().apply {
                put("id", UUID.randomUUID().toString())
                put("created_at", Instant.now().toString())
                put("event", event.toJsonObject())
            }.toString()
        })
        compactIfNeeded()
    }

    @Synchronized
    fun readAll(): List<QueuedLifeEvent> {
        if (!queueFile.exists()) return emptyList()

        return queueFile.readLines()
            .asSequence()
            .filter { it.isNotBlank() }
            .mapNotNull { line ->
                runCatching {
                    val json = JSONObject(line)
                    QueuedLifeEvent(
                        id = json.getString("id"),
                        createdAt = json.optString("created_at"),
                        event = lifeEventFromJsonObject(json.getJSONObject("event")),
                    )
                }.getOrNull()
            }
            .toList()
    }

    @Synchronized
    fun replace(items: List<QueuedLifeEvent>) {
        if (items.isEmpty()) {
            if (queueFile.exists()) queueFile.delete()
            return
        }

        val tempFile = File("${queueFile.absolutePath}.tmp")
        tempFile.writeText(items.joinToString(separator = "\n", postfix = "\n") { item ->
            JSONObject().apply {
                put("id", item.id)
                put("created_at", item.createdAt)
                put("event", item.event.toJsonObject())
            }.toString()
        })
        tempFile.copyTo(queueFile, overwrite = true)
        tempFile.delete()
    }

    @Synchronized
    fun size(): Int = readAll().size

    private fun compactIfNeeded() {
        val items = readAll()
        if (items.size <= MAX_QUEUED_EVENTS) return
        replace(items.takeLast(MAX_QUEUED_EVENTS))
    }

    data class QueuedLifeEvent(
        val id: String,
        val createdAt: String,
        val event: LifeEvent,
    )

    companion object {
        private const val QUEUE_FILE_NAME = "life-events.ndjson"
        private const val MAX_QUEUED_EVENTS = 5_000
    }
}
