package com.ailife.android.health

import android.content.Context
import com.ailife.android.data.SettingsStore
import com.ailife.android.generated.VersionedEvent
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.temporal.ChronoUnit

/**
 * Collects Health Connect observations and turns them into versioned contract
 * events:
 *
 * - the existing Health Connect permission flow is reused unchanged; when no
 *   read permission is granted the pass contributes nothing instead of
 *   failing;
 * - the query window is the collector watermark minus a bounded overlap, or
 *   [INITIAL_LOOKBACK_DAYS] on the first pass, so a fresh install backfills
 *   recent history and an offline collector re-reads its gap;
 * - every sample keeps its Health Connect record identity and data origin;
 * - planning results and revisions persist in [HealthConnectSyncState], and
 *   the produced events land in the durable outbox handled by the sync
 *   worker.
 */
class HealthConnectEventSource(
    private val context: Context,
    private val settings: SettingsStore,
    private val nowMillis: () -> Long = System::currentTimeMillis,
) {
    suspend fun collectPendingEvents(): List<VersionedEvent> {
        val now = Instant.ofEpochMilli(nowMillis())
        if (!HealthConnectCollector.isAvailable(context)) return emptyList()

        val collector = HealthConnectCollector(context)
        val granted = runCatching { collector.grantedPermissions() }.getOrDefault(emptySet())
        if (granted.isEmpty()) return emptyList() // Permission gate stays a UI concern; no silent failure.

        val since = sinceInstant(now)
        val readOutcome = runCatching { collector.readSamples(since, now) }
        val samples = readOutcome.getOrDefault(emptyList())

        val state = HealthConnectSyncState(stateFile(context))
        val plan = HealthConnectEventPlanner.plan(
            samples = samples,
            state = state.records,
            deviceId = settings.deviceId,
            ownerId = settings.ownerId,
            installGuid = state.installGuid,
            nowMillis = now.toEpochMilli(),
            collectorVersion = resolveCollectorVersion(),
            zone = ZoneId.systemDefault(),
        )

        for ((eventId, recordState) in plan.states) {
            state.record(eventId, recordState)
        }
        state.pruneEndedBefore(now.toEpochMilli() - STATE_RETENTION_MS)
        state.save()

        // Advance the collector watermark only after a successful read so a
        // failed Health Connect query re-reads its window on the next pass.
        if (readOutcome.isSuccess) {
            settings.lastHealthSyncMillis = now.toEpochMilli()
        }
        return plan.events
    }

    private fun sinceInstant(now: Instant): Instant {
        val lastSync = settings.lastHealthSyncMillis
        return if (lastSync <= 0) {
            now.minus(INITIAL_LOOKBACK_DAYS, ChronoUnit.DAYS)
        } else {
            Instant.ofEpochMilli(lastSync).minus(WATERMARK_OVERLAP_MINUTES, ChronoUnit.MINUTES)
        }
    }

    private fun resolveCollectorVersion(): String {
        return try {
            val packageInfo = context.packageManager.getPackageInfo(context.packageName, 0)
            packageInfo.versionName?.takeIf { version ->
                Regex("^[0-9]+\\.[0-9]+\\.[0-9]+").containsMatchIn(version)
            } ?: "0.0.0"
        } catch (_: Exception) {
            "0.0.0"
        }
    }

    companion object {
        private const val INITIAL_LOOKBACK_DAYS = 7L
        private const val WATERMARK_OVERLAP_MINUTES = 5L
        private const val STATE_RETENTION_MS = 7L * 24 * 60 * 60 * 1000
        private const val STATE_FILE_NAME = "health-sync-state.json"

        fun stateFile(context: Context) = File(context.filesDir, STATE_FILE_NAME)
    }
}
