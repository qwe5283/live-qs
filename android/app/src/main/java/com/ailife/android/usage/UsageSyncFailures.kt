package com.ailife.android.usage

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.time.Instant

/** One permanently rejected upload, kept locally so failures stay visible. */
@Serializable
data class UsageSyncFailure(
    val eventId: String,
    val revision: Long,
    val errorCode: String,
    val message: String,
    val recordedAt: String,
)

/**
 * Local failure queue for permanently rejected events (stable error codes such
 * as `invalid_event`, `event_type_not_allowed`, or `privacy_ceiling_exceeded`).
 * Rejections are never retried; the queue surfaces them to the Owner instead
 * of silently discarding collection results.
 */
class UsageSyncFailures(private val file: File) {
    private val json = Json { ignoreUnknownKeys = true }

    @Synchronized
    fun record(eventId: String, revision: Long, errorCode: String, message: String) {
        file.parentFile?.mkdirs()
        file.appendText(
            json.encodeToString(
                UsageSyncFailure(
                    eventId = eventId,
                    revision = revision,
                    errorCode = errorCode,
                    message = message.take(MAX_MESSAGE_LENGTH),
                    recordedAt = Instant.now().toString(),
                ),
            ) + "\n",
        )
    }

    @Synchronized
    fun readAll(): List<UsageSyncFailure> {
        if (!file.exists()) return emptyList()
        return file.readLines()
            .asSequence()
            .filter { it.isNotBlank() }
            .mapNotNull { line -> runCatching { json.decodeFromString<UsageSyncFailure>(line) }.getOrNull() }
            .toList()
    }

    @Synchronized
    fun size(): Int = readAll().size

    private companion object {
        private const val MAX_MESSAGE_LENGTH = 300
    }
}
