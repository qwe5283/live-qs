package com.ailife.android.usage

import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import com.ailife.android.data.SettingsStore
import com.ailife.android.generated.ActivityIntervalEventV1
import com.ailife.android.health.UsageStatsCollector
import java.io.File
import java.time.ZoneId

/**
 * Collects the authoritative Android daily application usage from the system
 * UsageStats feed and turns it into versioned contract events:
 *
 * - the existing Usage Access permission gate is reused unchanged;
 * - every pass re-derives foreground sessions from the OS event log since the
 *   last pass (minus a bounded lookback), so nothing depends on collector
 *   liveness: sessions that closed while the process was dead are still
 *   finalized on the next pass;
 * - the query watermark plus the tracked open-session starts bound the window,
 *   so an offline collector backfills everything since its last pass;
 * - planning results and revisions persist in [UsageStatsSyncState], and the
 *   produced events land in the durable outbox handled by the sync worker.
 */
class UsageStatsEventSource(
    private val context: Context,
    private val settings: SettingsStore,
    private val nowMillis: () -> Long = System::currentTimeMillis,
) {
    fun collectPendingEvents(): List<ActivityIntervalEventV1> {
        if (!UsageStatsCollector.hasUsageAccess(context)) return emptyList()

        val now = nowMillis()
        val state = UsageStatsSyncState(stateFile(context))
        val queryStartMillis = queryStartMillis(state, now)
        val transitions = queryTransitions(queryStartMillis, now)
        val intervals = UsageStatsIntervals.build(transitions)

        val plan = UsageStatsEventPlanner.plan(
            intervals = intervals,
            state = UsageStatsSyncStateView(installGuid = state.installGuid, intervals = state.intervals),
            deviceId = settings.deviceId,
            ownerId = settings.ownerId,
            nowMillis = now,
            collectorVersion = resolveCollectorVersion(),
            zone = ZoneId.systemDefault(),
            appNameOf = this::resolveAppName,
        )

        for ((eventId, intervalState) in plan.states) {
            state.record(eventId, intervalState)
        }
        state.lastSyncEndMillis = now
        state.pruneFinalizedBefore(now - STATE_RETENTION_MS)
        state.save()
        return plan.events
    }

    /**
     * Re-queries far enough to cover fresh sessions, late-arriving system
     * events, sessions open across an outage, and the open session that may
     * have started before any recent pass.
     */
    private fun queryStartMillis(state: UsageStatsSyncState, now: Long): Long {
        var queryStart = now - LOOKBACK_MS
        if (state.lastSyncEndMillis > 0) {
            queryStart = minOf(queryStart, state.lastSyncEndMillis - WATERMARK_OVERLAP_MS)
        }
        state.oldestOpenStartMillis?.let { oldest -> queryStart = minOf(queryStart, oldest) }
        return queryStart
    }

    private fun queryTransitions(startMillis: Long, endMillis: Long): List<UsageTransition> {
        val usageStatsManager = context.getSystemService(Context.USAGE_STATS_SERVICE) as UsageStatsManager
        val events = usageStatsManager.queryEvents(startMillis, endMillis)
        val event = UsageEvents.Event()
        val transitions = mutableListOf<UsageTransition>()
        while (events.hasNextEvent()) {
            events.getNextEvent(event)
            val packageName = event.packageName?.takeIf { it.isNotBlank() } ?: continue
            val isResume = when (event.eventType) {
                UsageEvents.Event.ACTIVITY_RESUMED -> true
                UsageEvents.Event.ACTIVITY_PAUSED, UsageEvents.Event.ACTIVITY_STOPPED -> false
                else -> continue
            }
            transitions.add(UsageTransition(packageName, event.timeStamp, isResume))
        }
        return transitions
    }

    private fun resolveAppName(packageName: String): String? {
        return try {
            context.packageManager.getApplicationInfo(packageName, 0)
                .let { context.packageManager.getApplicationLabel(it).toString() }
                .takeIf { it.isNotBlank() }
        } catch (_: Exception) {
            null
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
        private const val LOOKBACK_MS = 48L * 60 * 60 * 1000
        private const val WATERMARK_OVERLAP_MS = 60L * 60 * 1000
        private const val STATE_RETENTION_MS = 7L * 24 * 60 * 60 * 1000
        private const val STATE_FILE_NAME = "usage-sync-state.json"

        fun stateFile(context: Context) = File(context.filesDir, STATE_FILE_NAME)
    }
}
