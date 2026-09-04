package com.ailife.android.data.queue

import com.ailife.android.generated.ActivityIntervalEventV1
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.time.Instant
import java.util.UUID

/**
 * Durable outbox for versioned contract events. Entries survive process
 * restarts and are removed only after their revision is acknowledged by the
 * server. Enqueueing is an upsert per logical event: a newer revision of an
 * event that is still pending replaces the stale pending copy, so an outage
 * followed by a rebuild never uploads superseded checkpoints.
 */
class UsageEventSpoolQueue(private val file: File) {
    private val json = Json { ignoreUnknownKeys = true }

    @Serializable
    private data class Entry(
        val id: String,
        val createdAt: String,
        val event: ActivityIntervalEventV1,
    )

    @Synchronized
    fun enqueueAll(events: List<ActivityIntervalEventV1>) {
        if (events.isEmpty()) return
        val existing = readEntries().associateByTo(mutableMapOf()) { it.event.eventId }
        for (event in events) {
            val pending = existing[event.eventId]
            if (pending != null && pending.event.revision >= event.revision) continue
            existing[event.eventId] = Entry(
                id = pending?.id ?: UUID.randomUUID().toString(),
                createdAt = pending?.createdAt ?: Instant.now().toString(),
                event = event,
            )
        }
        writeEntries(existing.values.sortedBy { it.event.startAt })
    }

    @Synchronized
    fun readAll(): List<SpooledUsageEvent> {
        return readEntries().map { SpooledUsageEvent(id = it.id, createdAt = it.createdAt, event = it.event) }
    }

    @Synchronized
    fun removeEventIds(eventIds: Set<String>) {
        if (eventIds.isEmpty()) return
        val retained = readEntries().filter { it.event.eventId !in eventIds }
        writeEntries(retained)
    }

    @Synchronized
    fun size(): Int = readEntries().size

    private fun readEntries(): List<Entry> {
        if (!file.exists()) return emptyList()
        return file.readLines()
            .asSequence()
            .filter { it.isNotBlank() }
            .mapNotNull { line -> runCatching { json.decodeFromString<Entry>(line) }.getOrNull() }
            .toList()
    }

    private fun writeEntries(entries: Collection<Entry>) {
        if (entries.isEmpty()) {
            if (file.exists()) file.delete()
            return
        }
        file.parentFile?.mkdirs()
        val tempFile = File("${file.absolutePath}.tmp")
        tempFile.writeText(entries.joinToString(separator = "\n", postfix = "\n") { entry -> json.encodeToString(entry) })
        tempFile.copyTo(file, overwrite = true)
        tempFile.delete()
    }
}

data class SpooledUsageEvent(
    val id: String,
    val createdAt: String,
    val event: ActivityIntervalEventV1,
)
