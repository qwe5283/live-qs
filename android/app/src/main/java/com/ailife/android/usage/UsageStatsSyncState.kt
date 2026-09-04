package com.ailife.android.usage

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.util.UUID

/** Last-reported interpretation of one logical usage session. */
@Serializable
data class UsageIntervalState(
    val packageName: String,
    val startMillis: Long,
    val endMillis: Long?,
    val revision: Long,
    val isFinal: Boolean,
)

/**
 * Durable bookkeeping for the UsageStats event path: the per-install GUID that
 * scopes every event identity, the query watermark, and the revision state of
 * each reconstructed session. All three live in one file so they share fate:
 * a wiped store regenerates the GUID too, so replays never collide with cloud
 * history through stale event identities. Revisions must survive process
 * restarts — a lost revision would regress checkpoints server-side.
 */
class UsageStatsSyncState(private val file: File) {
    private val json = Json

    @Serializable
    private data class Snapshot(
        val installGuid: String,
        val lastSyncEndMillis: Long,
        val intervals: Map<String, UsageIntervalState>,
    )

    private var snapshot: Snapshot

    init {
        val loaded = readSnapshot()
        if (loaded == null) {
            snapshot = Snapshot(
                installGuid = UUID.randomUUID().toString(),
                lastSyncEndMillis = 0L,
                intervals = emptyMap(),
            )
            // The identity epoch is durable from the moment it is minted so a
            // crash before the first planning pass cannot fork identities.
            save()
        } else {
            snapshot = loaded
        }
    }

    val installGuid: String get() = snapshot.installGuid
    var lastSyncEndMillis: Long
        get() = snapshot.lastSyncEndMillis
        set(value) {
            snapshot = snapshot.copy(lastSyncEndMillis = value)
        }
    val intervals: Map<String, UsageIntervalState> get() = snapshot.intervals

    /** The start of the oldest still-open session, or null when none is tracked. */
    val oldestOpenStartMillis: Long?
        get() = snapshot.intervals.values
            .filter { it.endMillis == null }
            .minOfOrNull { it.startMillis }

    fun record(eventId: String, state: UsageIntervalState) {
        snapshot = snapshot.copy(intervals = snapshot.intervals + (eventId to state))
    }

    /**
     * Forgets finalized sessions that ended before the retention horizon. The
     * outbox keeps pending items independently, so only long-acked history is
     * pruned here; open sessions always survive to receive their final
     * revision.
     */
    fun pruneFinalizedBefore(horizonMillis: Long) {
        val retained = snapshot.intervals.filterValues { state ->
            state.endMillis == null || !state.isFinal || state.endMillis >= horizonMillis
        }
        snapshot = snapshot.copy(intervals = retained)
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
