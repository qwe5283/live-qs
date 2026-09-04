package com.ailife.android.health

import com.ailife.android.generated.EventType
import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.SourceKind
import com.ailife.android.generated.StepUnit
import java.time.Instant
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class HealthConnectEventPlannerTest {
    private val zone = ZoneId.of("Asia/Shanghai")
    private val deviceId = "phone"
    private val installGuid = "2a8f6b34-1f9e-4d0a-9f52-6f3ba0c1d7e8"
    private val walkStart = 1_755_698_200_000L // 2026-08-20T01:30:00Z
    private val walkEnd = 1_755_700_200_000L

    private fun plan(
        samples: List<HealthSample>,
        state: Map<String, HealthRecordState> = emptyMap(),
        nowMillis: Long = walkEnd + 60_000,
    ) = HealthConnectEventPlanner.plan(
        samples = samples,
        state = state,
        deviceId = deviceId,
        ownerId = "local",
        installGuid = installGuid,
        nowMillis = nowMillis,
        collectorVersion = "0.2.0",
        zone = zone,
    )

    @Test
    fun stepsCarryTheirOriginCountUnitAndIntervalBounds() {
        val sample = HealthStepsSample("hc-steps-1", "com.google.android.apps.fitness", walkStart, walkEnd, count = 2415)

        val event = plan(listOf(sample)).events.single()

        assertEquals(EventType.HEALTH_STEP_SAMPLE, event.eventType)
        assertEquals(1L, event.revision)
        assertEquals(FinalizationState.FINAL, event.finalizationState)
        assertEquals(Instant.ofEpochMilli(walkStart).toString(), event.startAt)
        assertEquals(Instant.ofEpochMilli(walkEnd).toString(), event.endAt)
        assertEquals(2415L, event.payload.count?.value)
        assertEquals(StepUnit.STEPS, event.payload.count?.unit)
        assertEquals("com.google.android.apps.fitness", event.payload.dataOrigin)
        assertEquals("android.healthconnect", event.source.kind.value)
        assertEquals("hc-steps-1", event.source.recordId)
        assertEquals(deviceId, event.device.id)
        assertEquals("android", event.device.platform.value)
        assertEquals("local", event.ownerId)
        assertEquals("Asia/Shanghai", event.captureTimezone)
        assertEquals(480L, event.captureOffsetMinutes)
        // Health observations default to sensitive data.
        assertEquals(PrivacyLevel.SENSITIVE, event.privacyLevel)
        assertEquals("0.2.0", event.provenance.collectorVersion)
        assertEquals(false, event.invalidated)
    }

    @Test
    fun heartRateIsAnInstantaneousSampleWithoutEndAt() {
        val sample = HealthHeartRateSample("hc-hr-1", "com.mi.health", walkStart, beatsPerMinute = 62)

        val event = plan(listOf(sample)).events.single()

        assertEquals(EventType.HEALTH_HEARTRATE_SAMPLE, event.eventType)
        assertNull(event.endAt)
        assertEquals(62L, event.payload.beatsPerMinute)
        assertEquals("com.mi.health", event.payload.dataOrigin)
        assertNull(event.payload.duration)
    }

    @Test
    fun sleepUsesOnlyTheSourceProvidedInterval() {
        val bedtime = 1_755_646_200_000L
        val wake = bedtime + 27_390_000L
        val sample = HealthSleepSample("hc-sleep-1", "com.urbandroid.sleep", bedtime, wake)

        val event = plan(listOf(sample)).events.single()

        assertEquals(EventType.HEALTH_SLEEP_SESSION, event.eventType)
        assertEquals(Instant.ofEpochMilli(bedtime).toString(), event.startAt)
        assertEquals(Instant.ofEpochMilli(wake).toString(), event.endAt)
        // The payload duration is derived from the reported bounds only.
        assertEquals(27_390_000L, event.payload.duration?.value)
        assertEquals("com.urbandroid.sleep", event.payload.dataOrigin)
    }

    @Test
    fun aChangedRecordFingerprintAdvancesTheRevisionOfTheSameLogicalEvent() {
        val sample = HealthStepsSample("hc-steps-1", "com.google.android.apps.fitness", walkStart, walkEnd, count = 2415)
        val firstPass = plan(listOf(sample))
        val eventId = firstPass.events.single().eventId

        val secondPass = plan(
            listOf(sample.copy(count = 2600)),
            state = firstPass.states,
        )

        assertEquals(eventId, secondPass.events.single().eventId)
        assertEquals(2L, secondPass.events.single().revision)
        assertEquals(2600L, secondPass.events.single().payload.count?.value)
    }

    @Test
    fun anUnchangedRecordIsSkippedInsteadOfReEmitted() {
        val sample = HealthStepsSample("hc-steps-1", "com.google.android.apps.fitness", walkStart, walkEnd, count = 2415)
        val firstPass = plan(listOf(sample))

        val secondPass = plan(listOf(sample), state = firstPass.states)

        assertTrue(secondPass.events.isEmpty())
        assertEquals(1, secondPass.unchangedCount)
    }

    @Test
    fun recordsFromDifferentOriginsKeepDistinctIdentities() {
        val phone = HealthStepsSample("hc-steps-phone", "com.google.android.apps.fitness", walkStart, walkEnd, count = 2415)
        val watch = HealthStepsSample("hc-steps-watch", "com.wearable.fitness", walkStart, walkEnd, count = 2400)

        val result = plan(listOf(phone, watch))

        assertEquals(2, result.events.size)
        assertNotEquals(result.events[0].eventId, result.events[1].eventId)
        val origins = result.events.map { it.payload.dataOrigin }.toSet()
        assertEquals(setOf("com.google.android.apps.fitness", "com.wearable.fitness"), origins)
    }

    @Test
    fun malformedRecordsAreDroppedAndCountedInsteadOfSilentlyKept() {
        val missingEnd = HealthStepsSample("hc-steps-bad", "com.origin", walkStart, endMillis = null, count = 100)
        val inverted = HealthSleepSample("hc-sleep-bad", "com.origin", walkStart, walkStart)
        val blankOrigin = HealthHeartRateSample("hc-hr-bad", "", walkStart, beatsPerMinute = 60)
        val blankRecordId = HealthHeartRateSample("", "com.origin", walkStart, beatsPerMinute = 60)

        val result = plan(listOf(missingEnd, inverted, blankOrigin, blankRecordId))

        assertTrue(result.events.isEmpty())
        assertEquals(4, result.droppedCount)
    }

    @Test
    fun identityIsStableAndScopedByDeviceInstallAndRecord() {
        val first = HealthEventIds.forRecord("health.step.sample", deviceId, installGuid, "hc-steps-1")
        assertEquals(first, HealthEventIds.forRecord("health.step.sample", deviceId, installGuid, "hc-steps-1"))
        assertNotEquals(first, HealthEventIds.forRecord("health.step.sample", deviceId, installGuid, "hc-steps-2"))
        assertNotEquals(first, HealthEventIds.forRecord("health.sleep.session", deviceId, installGuid, "hc-steps-1"))
        assertNotEquals(first, HealthEventIds.forRecord("health.step.sample", "tablet", installGuid, "hc-steps-1"))
        // A different install GUID means a reinstall or data wipe: a new
        // identity epoch that never collides with uploaded history.
        assertNotEquals(first, HealthEventIds.forRecord("health.step.sample", deviceId, "install-2", "hc-steps-1"))
        assertFalse(first.toString().isBlank())
    }
}
