package com.ailife.android.payment

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.util.UUID

/** Last-reported interpretation of one parsed payment notification. */
@Serializable
data class WechatTransactionState(
    val eventId: String,
    val recordId: String,
    val revision: Long,
    val fingerprint: String,
    val timeMillis: Long,
    /** Contract direction value ("income"/"expense") used for duplicate suspicion. */
    val direction: String,
    val amountMinor: Long,
    val merchant: String,
)

/**
 * Durable bookkeeping for the WeChat payment event path: the per-install GUID
 * that scopes every event identity and the revision/fingerprint state of each
 * reported notification. Both live in one file so they share fate — a wiped
 * store regenerates the GUID too, so replays never collide with cloud history
 * through stale event identities. The recent-transaction facts also power the
 * suspected-duplicate detection across process restarts; older entries are
 * pruned after the retention window.
 */
class WechatPaySyncState(private val file: File) {
    private val json = Json

    @Serializable
    private data class Snapshot(
        val installGuid: String,
        val transactions: Map<String, WechatTransactionState>,
    )

    private var snapshot: Snapshot

    init {
        val loaded = readSnapshot()
        if (loaded == null) {
            snapshot = Snapshot(
                installGuid = UUID.randomUUID().toString(),
                transactions = emptyMap(),
            )
            // The identity epoch is durable from the moment it is minted so a
            // crash before the first planning pass cannot fork identities.
            save()
        } else {
            snapshot = loaded
        }
    }

    val installGuid: String get() = snapshot.installGuid
    val transactions: Map<String, WechatTransactionState> get() = snapshot.transactions

    fun record(eventId: String, state: WechatTransactionState) {
        snapshot = snapshot.copy(transactions = snapshot.transactions + (eventId to state))
    }

    /**
     * Forgets notifications older than the retention horizon. The outbox keeps
     * pending items independently; a notification re-parsed after pruning
     * re-emits revision 1 and is answered as a duplicate server-side.
     */
    fun prunePostedBefore(horizonMillis: Long) {
        val retained = snapshot.transactions.filterValues { state -> state.timeMillis >= horizonMillis }
        snapshot = snapshot.copy(transactions = retained)
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
