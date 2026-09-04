package com.ailife.android.health

import com.ailife.android.generated.Count
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
import com.ailife.android.generated.StepUnit
import com.ailife.android.generated.VersionedEvent
import java.time.Instant
import java.time.ZoneId

/** Outcome of one planning pass over the collected Health Connect records. */
data class HealthEventPlan(
    val events: List<VersionedEvent>,
    val states: Map<String, HealthRecordState>,
    /** Well-formed records skipped because an identical fingerprint was already reported. */
    val unchangedCount: Int,
    /** Malformed records dropped before the upload path (never silently lost: the pass reports them). */
    val droppedCount: Int,
)

/**
 * Turns collected Health Connect records into contract events with stable
 * identities and monotonic revisions:
 *
 * - identity: UUIDv5 over event type + device id + install GUID + Health
 *   Connect record id, so replays and redeliveries address one logical event;
 * - revision 1 for the first report of a record; when the origin application
 *   rewrites a record (changed fingerprint) the next pass emits a higher
 *   revision — server semantics decide accepted/duplicate/stale, and nothing
 *   from another origin ever deletes this observation;
 * - an unchanged record is skipped — redelivery is the outbox's job, not a
 *   re-emission per pass;
 * - every event carries `data_origin` (the Health Connect writing
 *   application) and defaults to the `sensitive` privacy level;
 * - sleep is emitted only as the source-provided interval — the planner never
 *   derives a session from device idle time.
 *
 * The planner is pure: records and state in, plan out. Durability belongs to
 * [HealthConnectSyncState]; Health Connect queries belong to
 * [HealthConnectCollector].
 */
object HealthConnectEventPlanner {
    fun plan(
        samples: List<HealthSample>,
        state: Map<String, HealthRecordState>,
        deviceId: String,
        ownerId: String,
        installGuid: String,
        nowMillis: Long,
        collectorVersion: String,
        zone: ZoneId,
    ): HealthEventPlan {
        val events = mutableListOf<VersionedEvent>()
        val states = mutableMapOf<String, HealthRecordState>()
        var unchanged = 0
        var dropped = 0

        for (sample in samples.sortedWith(compareBy({ it.startMillis }, { it.recordId }))) {
            val recordId = sample.recordId
            if (recordId.isBlank() || sample.dataOrigin.isBlank()) {
                dropped += 1
                continue
            }
            if (!isWellFormed(sample)) {
                dropped += 1
                continue
            }

            val eventId = HealthEventIds.forRecord(sample.kind.eventType, deviceId, installGuid, recordId)
            val fingerprint = sample.fingerprint()
            val previous = state[eventId.toString()]
            if (previous != null && previous.fingerprint == fingerprint) {
                states[eventId.toString()] = previous
                unchanged += 1
                continue
            }
            val revision = (previous?.revision ?: 0L) + 1L

            val startInstant = Instant.ofEpochMilli(sample.startMillis)
            events.add(
                VersionedEvent(
                    eventType = eventTypeOf(sample.kind),
                    payload = payloadOf(sample),
                    privacyLevel = PrivacyLevel.SENSITIVE,
                    schemaVersion = 1,
                    source = Source(kind = SourceKind.ANDROID_HEALTHCONNECT, recordId = recordId),
                    captureOffsetMinutes = zone.rules.getOffset(startInstant).totalSeconds / 60L,
                    captureTimezone = zone.id,
                    device = Device(id = deviceId, platform = Platform.ANDROID),
                    endAt = sample.endMillis?.let { Instant.ofEpochMilli(it).toString() },
                    eventId = eventId.toString(),
                    finalizationState = FinalizationState.FINAL,
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
            states[eventId.toString()] = HealthRecordState(
                recordId = recordId,
                eventType = sample.kind.eventType,
                revision = revision,
                fingerprint = fingerprint,
                endMillis = sample.endMillis,
            )
        }

        return HealthEventPlan(events = events, states = states, unchangedCount = unchanged, droppedCount = dropped)
    }

    private fun isWellFormed(sample: HealthSample): Boolean = when (sample) {
        is HealthHeartRateSample -> true // Instantaneous: no end bound to validate.
        is HealthStepsSample -> sample.endMillis != null && sample.endMillis!! > sample.startMillis && sample.count >= 0
        is HealthSleepSample -> sample.endMillis != null && sample.endMillis!! > sample.startMillis
    }

    private fun payloadOf(sample: HealthSample): Payload = when (sample) {
        is HealthStepsSample -> Payload(
            count = Count(value = sample.count, unit = StepUnit.STEPS),
            dataOrigin = sample.dataOrigin,
        )
        is HealthHeartRateSample -> Payload(
            beatsPerMinute = sample.beatsPerMinute,
            dataOrigin = sample.dataOrigin,
        )
        is HealthSleepSample -> Payload(
            duration = Duration(value = (sample.endMillis ?: 0L) - sample.startMillis, unit = DurationUnit.MS),
            dataOrigin = sample.dataOrigin,
        )
    }

    private fun eventTypeOf(kind: HealthSampleKind): EventType = when (kind) {
        HealthSampleKind.STEPS -> EventType.HEALTH_STEP_SAMPLE
        HealthSampleKind.HEART_RATE -> EventType.HEALTH_HEARTRATE_SAMPLE
        HealthSampleKind.SLEEP -> EventType.HEALTH_SLEEP_SESSION
    }
}
