package com.ailife.android.update

import java.io.File
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/**
 * Persists the last update-check outcome in a small JSON state file so the
 * status screen and a restarted process always show the same diagnosable
 * state. A corrupted file degrades to the empty state, costing nothing more
 * than one fresh check.
 */
class UpdateCheckStateStore(directory: File) {
    private val stateFile = File(directory, FILE_NAME)

    @Serializable
    private data class Record(
        val state: String? = null,
        val availableVersion: String? = null,
        val releasedAt: String? = null,
        val downloadUrl: String? = null,
        val lastCheckAtMillis: Long? = null,
        val errorCode: String? = null,
        val errorMessage: String? = null,
    )

    fun read(): UpdateCheckSnapshot {
        val record = try {
            json.decodeFromString<Record>(stateFile.readText())
        } catch (_: Exception) {
            return UpdateCheckSnapshot(UpdateCheckState.IDLE)
        }
        val state = record.state?.let { name ->
            UpdateCheckState.entries.firstOrNull { it.name == name }
        } ?: UpdateCheckState.IDLE
        return UpdateCheckSnapshot(
            state = state,
            availableVersion = record.availableVersion,
            releasedAt = record.releasedAt,
            downloadUrl = record.downloadUrl,
            lastCheckAtMillis = record.lastCheckAtMillis,
            errorCode = record.errorCode,
            errorMessage = record.errorMessage,
        )
    }

    fun write(snapshot: UpdateCheckSnapshot) {
        stateFile.writeText(
            json.encodeToString(
                Record(
                    state = snapshot.state.name,
                    availableVersion = snapshot.availableVersion,
                    releasedAt = snapshot.releasedAt,
                    downloadUrl = snapshot.downloadUrl,
                    lastCheckAtMillis = snapshot.lastCheckAtMillis,
                    errorCode = snapshot.errorCode,
                    errorMessage = snapshot.errorMessage,
                ),
            ),
        )
    }

    private companion object {
        const val FILE_NAME = "update-check-state.json"

        val json = Json { encodeDefaults = true }
    }
}
