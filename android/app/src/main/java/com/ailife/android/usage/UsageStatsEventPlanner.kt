package com.ailife.android.usage

import com.ailife.android.generated.ActivityIntervalEventV1
import com.ailife.android.generated.Device
import com.ailife.android.generated.Duration
import com.ailife.android.generated.DurationUnit
import com.ailife.android.generated.EventType
import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.Payload
import com.ailife.android.generated.Platform
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.Provenance
import com.ailife.android.generated.Source
import com.ailife.android.generated.SourceKind
import java.time.Instant
import java.time.ZoneId

/**
 * Privacy resolution for package usage facts. V1 usage observations carry only
 * package names and durations, so they are `normal`; the seam exists so a
 * `private` designation can be enforced client-side: a private observation is
 * dropped before it can reach the upload spool (SPEC: a private event is
 * blocked before upload) and is never encoded into the contract envelope.
 */
const val USAGE_PRIVACY_NORMAL = "normal"
const val USAGE_PRIVACY_SENSITIVE = "sensitive"
const val USAGE_PRIVACY_PRIVATE = "private"

/** Outcome of one planning pass over the rebuilt session set. */
data class UsageEventPlan(
    val events: List<ActivityIntervalEventV1>,
    val states: Map<String, UsageIntervalState>,
    val droppedPrivateCount: Int,
)

/**
 * Turns reconstructed foreground sessions into contract `activity.interval.v1`
 * events with stable identities and monotonic revisions:
 *
 * - identity: UUIDv5 over device id + install GUID + package + session start,
 *   so replays, checkpoints, and restarts address the same logical event;
 * - revision 1 is the first checkpoint of an open session (end = observation
 *   instant), each extension and the final close increment the revision;
 * - an unchanged finalized session is skipped — redelivery is handled by the
 *   outbox, not by re-emitting every pass.
 *
 * The planner is pure: state in, plan out. Durability belongs to
 * [UsageStatsSyncState]; UsageStats queries belong to [UsageStatsEventSource].
 */
object UsageStatsEventPlanner {
    fun plan(
        intervals: List<UsageInterval>,
        state: UsageStatsSyncStateView,
        deviceId: String,
        ownerId: String,
        nowMillis: Long,
        collectorVersion: String,
        zone: ZoneId,
        appNameOf: (String) -> String? = { null },
        privacyLevelOf: (String) -> String = { USAGE_PRIVACY_NORMAL },
    ): UsageEventPlan {
        val events = mutableListOf<ActivityIntervalEventV1>()
        val states = mutableMapOf<String, UsageIntervalState>()
        var droppedPrivate = 0

        for (interval in intervals.sortedWith(compareBy({ it.startMillis }, { it.packageName }))) {
            if (interval.endMillis != null && interval.endMillis <= interval.startMillis) continue
            val isFinal = interval.endMillis != null
            // An open session is checkpointed up to the observation instant so
            // metrics can count its duration progressively.
            val endMillis = interval.endMillis ?: nowMillis
            if (endMillis <= interval.startMillis) continue

            val eventId = UsageEventIds.forSession(deviceId, state.installGuid, interval.packageName, interval.startMillis)
            val previous = state.intervals[eventId.toString()]
            if (previous != null && previous.isFinal && isFinal
                && previous.endMillis == interval.endMillis) {
                states[eventId.toString()] = previous
                continue // Already reported at these bounds; nothing changed.
            }
            val revision = (previous?.revision ?: 0L) + 1L
            if (privacyLevelOf(interval.packageName) == USAGE_PRIVACY_PRIVATE) {
                droppedPrivate += 1
                continue // Client-side privacy block: never enters the upload path.
            }

            val startInstant = Instant.ofEpochMilli(interval.startMillis)
            val endInstant = Instant.ofEpochMilli(endMillis)
            events.add(
                ActivityIntervalEventV1(
                    eventType = EventType.ACTIVITY_INTERVAL,
                    payload = Payload(
                        applicationId = interval.packageName,
                        applicationLabel = appNameOf(interval.packageName),
                        duration = Duration(value = endMillis - interval.startMillis, unit = DurationUnit.MS),
                        isAfk = false, // UsageStats intervals are foreground usage by definition.
                    ),
                    privacyLevel = if (privacyLevelOf(interval.packageName) == USAGE_PRIVACY_SENSITIVE) {
                        PrivacyLevel.SENSITIVE
                    } else {
                        PrivacyLevel.NORMAL
                    },
                    schemaVersion = 1,
                    source = Source(
                        kind = SourceKind.ANDROID_USAGESTATS,
                        recordId = "usage-session-${interval.packageName}-${interval.startMillis}",
                    ),
                    captureOffsetMinutes = zone.rules.getOffset(startInstant).totalSeconds / 60L,
                    captureTimezone = zone.id,
                    device = Device(id = deviceId, platform = Platform.ANDROID),
                    endAt = endInstant.toString(),
                    eventId = eventId.toString(),
                    finalizationState = if (isFinal) FinalizationState.FINAL else FinalizationState.CHECKPOINT,
                    invalidated = false,
                    ownerId = ownerId,
                    provenance = Provenance(
                        collectorVersion = collectorVersion,
                        observedAt = Instant.ofEpochMilli(nowMillis).toString(),
                    ),
                    revision = revision,
                    startAt = startInstant.toString(),
                ),
            )
            states[eventId.toString()] = UsageIntervalState(
                packageName = interval.packageName,
                startMillis = interval.startMillis,
                endMillis = interval.endMillis,
                revision = revision,
                isFinal = isFinal,
            )
        }

        return UsageEventPlan(events = events, states = states, droppedPrivateCount = droppedPrivate)
    }
}

/** Read-only view of [UsageStatsSyncState] the planner consumes. */
data class UsageStatsSyncStateView(
    val installGuid: String,
    val intervals: Map<String, UsageIntervalState>,
)
