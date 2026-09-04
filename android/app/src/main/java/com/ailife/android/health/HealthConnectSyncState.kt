package com.ailife.android.health

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.util.UUID

/** Last-reported interpretation of one logical Health Connect record. */
@Serializable
data class HealthRecordState(
    val recordId: String,
    val eventType: String,
    val revision: Long,
    val fingerprint: String,
    /** Millisecond end bound used for retention pruning; null for instantaneous samples. */
    val endMillis: Long?,
)

/**
 * Durable bookkeeping for the Health Connect event path: the per-install GUID
 * that scopes every event identity and the revision/fingerprint state of each
 * reported record. Both live in one file so they share fate — a wiped store
 * regenerates the GUID too, so replays never collide with cloud history
 * through stale event identities. Revisions must survive process restarts; a
 * lost revision would regress server-side facts instead of advancing them.
 */
class HealthConnectSyncState(private val file: File) {
    private val json = Json

    @Serializable
    private data class Snapshot(
        val installGuid: String,
        val records: Map<String, HealthRecordState>,
    )

    private var snapshot: Snapshot

    init {
        val loaded = readSnapshot()
        if (loaded == null) {
            snapshot = Snapshot(
                installGuid = UUID.randomUUID().toString(),
                records = emptyMap(),
            )
            // The identity epoch is durable from the moment it is minted so a
            // crash before the first planning pass cannot fork identities.
            save()
        } else {
            snapshot = loaded
        }
    }

    val installGuid: String get() = snapshot.installGuid
    val records: Map<String, HealthRecordState> get() = snapshot.records

    fun record(eventId: String, state: HealthRecordState) {
        snapshot = snapshot.copy(records = snapshot.records + (eventId to state))
    }

    /**
     * Forgets reported records that ended before the retention horizon. The
     * outbox keeps pending items independently, so only long-acked history is
     * pruned here; a record re-read after pruning is planned as a new identity
     * epoch (the GUID is unchanged, so the identity is stable — only the
     * fingerprint state is gone, which would re-emit the same revision 1 and
     * be acknowledged as a duplicate).
     */
    fun pruneEndedBefore(horizonMillis: Long) {
        val retained = snapshot.records.filterValues { state ->
            (state.endMillis ?: Long.MAX_VALUE) >= horizonMillis
        }
        snapshot = snapshot.copy(records = retained)
    }

    fun save() {
        file.parentFile?.mkdirs()
        val tempFile = File("${file.absolutePath}.tmp")
        tempFile.writeText(json.encodeToString(snapshot))
        tempFile.copyTo(file, overwrite = true)
        tempFile.delete()
    }

    private fun readSnapshot(): Snapshot? {
        if (!file.exists()) return null
        return runCatching { json.decodeFromString<Snapshot>(file.readText()) }.getOrNull()
    }
}
