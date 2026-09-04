package com.ailife.android.payment

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.time.Instant

/**
 * One unparseable WeChat payment notification, kept for LOCAL diagnosis only.
 * The raw notification title and text exist exclusively inside this on-device
 * queue: they never enter the upload outbox, server events, or logs. The sync
 * screen surfaces only the failure count.
 */
@Serializable
data class PaymentNotificationFailure(
    val sourceFingerprint: String,
    val reason: String,
    val postedAt: String,
    val title: String,
    val text: String,
    val recordedAt: String,
)

/**
 * Local failure queue for payment notifications that mentioned payment
 * keywords but could not be parsed into exact contract facts. Entries are
 * never uploaded and never guessed on the Owner's behalf; each notification
 * fingerprint is recorded once, and the queue is capped so a noisy source
 * cannot grow it without bound.
 */
class PaymentNotificationFailures(private val file: File, private val capacity: Int = DEFAULT_CAPACITY) {
    private val json = Json { ignoreUnknownKeys = true }

    @Synchronized
    fun record(failure: PaymentNotificationFailure) {
        file.parentFile?.mkdirs()
        if (file.exists() && readAll().any { it.sourceFingerprint == failure.sourceFingerprint }) return
        file.appendText(json.encodeToString(failure) + "\n")
        trim()
    }

    @Synchronized
    fun readAll(): List<PaymentNotificationFailure> {
        if (!file.exists()) return emptyList()
        return file.readLines()
            .asSequence()
            .filter { it.isNotBlank() }
            .mapNotNull { line -> runCatching { json.decodeFromString<PaymentNotificationFailure>(line) }.getOrNull() }
            .toList()
    }

    @Synchronized
    fun size(): Int = readAll().size

    /** Keeps the newest [capacity] entries so diagnosis stays bounded. */
    private fun trim() {
        val entries = readAll()
        if (entries.size <= capacity) return
        val tempFile = File("${file.absolutePath}.tmp")
        tempFile.writeText(entries.takeLast(capacity).joinToString(separator = "\n", postfix = "\n") { entry -> json.encodeToString(entry) })
        tempFile.copyTo(file, overwrite = true)
        tempFile.delete()
    }

    private companion object {
        private const val DEFAULT_CAPACITY = 200
    }
}
