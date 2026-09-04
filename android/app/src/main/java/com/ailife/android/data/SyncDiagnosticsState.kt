package com.ailife.android.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.time.Instant

/** Stable-code, safe-summary record of the most recent transient sync failure. */
@Serializable
data class TransientSyncError(
    val code: String,
    val message: String,
    val occurredAt: String,
)

/**
 * Device-local sync facts the diagnostics snapshot is built from. Unlike the
 * outbox and the failure queue, these are observations the collector makes
 * about its own delivery; they are persisted so a snapshot pushed after a
 * process restart reports the same state the previous process left behind.
 * Only timestamps and stable codes live here — never event content.
 */
@Serializable
data class SyncDiagnosticsSnapshotState(
    val lastCollectionAt: String? = null,
    val lastSuccessfulUploadAt: String? = null,
    val lastTransientError: TransientSyncError? = null,
)

class SyncDiagnosticsState(private val file: File) {
    private val json = Json { ignoreUnknownKeys = true }

    @Synchronized
    fun read(): SyncDiagnosticsSnapshotState {
        if (!file.exists()) return SyncDiagnosticsSnapshotState()
        return runCatching { json.decodeFromString<SyncDiagnosticsSnapshotState>(file.readText()) }
            .getOrDefault(SyncDiagnosticsSnapshotState())
    }

    @Synchronized
    fun recordCollection(at: Instant = Instant.now()) {
        write(read().copy(lastCollectionAt = at.toString()))
    }

    @Synchronized
    fun recordSuccessfulUpload(at: Instant = Instant.now()) {
        write(read().copy(lastSuccessfulUploadAt = at.toString()))
    }

    @Synchronized
    fun recordTransientError(error: TransientSyncError) {
        write(read().copy(lastTransientError = error))
    }

    private fun write(state: SyncDiagnosticsSnapshotState) {
        file.parentFile?.mkdirs()
        file.writeText(json.encodeToString(state))
    }
}
