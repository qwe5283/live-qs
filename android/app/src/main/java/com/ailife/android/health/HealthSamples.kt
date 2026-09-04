package com.ailife.android.health

import java.security.MessageDigest

/** The Health Connect record types the collector ingests. */
enum class HealthSampleKind(val eventType: String) {
    STEPS("health.step.sample"),
    HEART_RATE("health.heartrate.sample"),
    SLEEP("health.sleep.session"),
}

/**
 * One record as provided by Health Connect, before it becomes a contract
 * event. Every sample keeps the originating application (the data origin) and
 * the stable Health Connect record identifier so source record counts can be
 * reconciled against server acknowledgements. `endMillis` is null only for
 * instantaneous samples (heart rate); the planner drops malformed records of
 * the other kinds.
 */
sealed interface HealthSample {
    val kind: HealthSampleKind
    val recordId: String
    val dataOrigin: String
    val startMillis: Long
    val endMillis: Long?
}

/** Cumulative steps over a bounded interval, as one origin application reported them. */
data class HealthStepsSample(
    override val recordId: String,
    override val dataOrigin: String,
    override val startMillis: Long,
    override val endMillis: Long?,
    val count: Long,
) : HealthSample {
    override val kind: HealthSampleKind = HealthSampleKind.STEPS
}

/** One instantaneous heart-rate measurement in beats per minute. */
data class HealthHeartRateSample(
    override val recordId: String,
    override val dataOrigin: String,
    override val startMillis: Long,
    val beatsPerMinute: Long,
) : HealthSample {
    override val kind: HealthSampleKind = HealthSampleKind.HEART_RATE
    override val endMillis: Long? = null
}

/** A sleep interval exactly as provided by the origin application; never inferred. */
data class HealthSleepSample(
    override val recordId: String,
    override val dataOrigin: String,
    override val startMillis: Long,
    override val endMillis: Long?,
) : HealthSample {
    override val kind: HealthSampleKind = HealthSampleKind.SLEEP
}

/**
 * Content fingerprint of one sample: a changed Health Connect record (the
 * origin application rewrote it) yields a different fingerprint, which moves
 * the logical event to its next revision; an identical record is never
 * re-emitted.
 */
fun HealthSample.fingerprint(): String {
    val value = when (this) {
        is HealthStepsSample -> "$startMillis|$endMillis|steps|$count"
        is HealthHeartRateSample -> "$startMillis||bpm|$beatsPerMinute"
        is HealthSleepSample -> "$startMillis|$endMillis|sleep"
    }
    val digest = MessageDigest.getInstance("SHA-256")
        .digest("$kind|$dataOrigin|$value".toByteArray(Charsets.UTF_8))
    return digest.joinToString(separator = "") { byte -> "%02x".format(byte) }
}
