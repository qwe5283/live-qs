package com.ailife.android.usage

import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.SourceKind
import java.time.Instant
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class UsageStatsEventPlannerTest {
    private val zone = ZoneId.of("Asia/Shanghai")
    private val deviceId = "phone"
    private val installGuid = "2a8f6b34-1f9e-4d0a-9f52-6f3ba0c1d7e8"
    private val sessionStart = 1_754_043_000_000L // 2026-08-01T13:30:00Z

    private fun stateView(intervals: Map<String, UsageIntervalState> = emptyMap()) =
        UsageStatsSyncStateView(installGuid = installGuid, intervals = intervals)

    private fun plan(
        intervals: List<UsageInterval>,
        state: UsageStatsSyncStateView,
        nowMillis: Long,
        privacyLevelOf: (String) -> String = { USAGE_PRIVACY_NORMAL },
    ) = UsageStatsEventPlanner.plan(
        intervals = intervals,
        state = state,
        deviceId = deviceId,
        ownerId = "local",
        nowMillis = nowMillis,
        collectorVersion = "0.1.0",
        zone = zone,
        appNameOf = { "应用" },
        privacyLevelOf = privacyLevelOf,
    )

    @Test
    fun firstPassOnAnOpenSessionEmitsRevisionOneCheckpointEndingAtTheObservationInstant() {
        val result = plan(
            intervals = listOf(UsageInterval("tv.danmaku.bili", sessionStart, null)),
            state = stateView(),
            nowMillis = sessionStart + 60_000,
        )

        val event = result.events.single()
        val expectedId = UsageEventIds.forSession(deviceId, installGuid, "tv.danmaku.bili", sessionStart)
        assertEquals(expectedId.toString(), event.eventId)
        assertEquals(1L, event.revision)
        assertEquals(FinalizationState.CHECKPOINT, event.finalizationState)
        assertEquals(Instant.ofEpochMilli(sessionStart).toString(), event.startAt)
        assertEquals(Instant.ofEpochMilli(sessionStart + 60_000).toString(), event.endAt)
        assertEquals(60_000L, event.payload.duration.value)
        assertEquals("ms", event.payload.duration.unit.value)
        assertEquals("tv.danmaku.bili", event.payload.applicationId)
        assertEquals(false, event.payload.isAfk)
        assertEquals("android.usagestats", event.source.kind.value)
        assertEquals("usage-session-tv.danmaku.bili-$sessionStart", event.source.recordId)
        assertEquals(deviceId, event.device.id)
        assertEquals("android", event.device.platform.value)
        assertEquals("local", event.ownerId)
        assertEquals("Asia/Shanghai", event.captureTimezone)
        assertEquals(480L, event.captureOffsetMinutes)
        assertEquals(PrivacyLevel.NORMAL, event.privacyLevel)
        assertEquals("0.1.0", event.provenance.collectorVersion)
        assertEquals(false, event.invalidated)

        val state = result.states.getValue(expectedId.toString())
        assertEquals(1L, state.revision)
        assertNull(state.endMillis)
        assertEquals(false, state.isFinal)
    }

    @Test
    fun extensionsAndFinalizationIncrementTheRevisionOfTheSameLogicalEvent() {
        val eventId = UsageEventIds.forSession(deviceId, installGuid, "tv.danmaku.bili", sessionStart).toString()

        val pass1 = plan(
            listOf(UsageInterval("tv.danmaku.bili", sessionStart, null)),
            stateView(),
            sessionStart + 60_000,
        )
        val pass2 = plan(
            listOf(UsageInterval("tv.danmaku.bili", sessionStart, null)),
            stateView(pass1.states),
            sessionStart + 120_000,
        )
        val pass3 = plan(
            listOf(UsageInterval("tv.danmaku.bili", sessionStart, sessionStart + 150_000)),
            stateView(pass2.states),
            sessionStart + 150_000,
        )

        // Same stable identity throughout; the revision climbs while the
        // session extends and at finalization.
        assertTrue(pass2.events.single().eventId == eventId && pass3.events.single().eventId == eventId)
        assertEquals(2L, pass2.events.single().revision)
        assertEquals(FinalizationState.CHECKPOINT, pass2.events.single().finalizationState)
        assertEquals(3L, pass3.events.single().revision)
        assertEquals(FinalizationState.FINAL, pass3.events.single().finalizationState)
        assertEquals(150_000L, pass3.events.single().payload.duration.value)
        assertTrue(pass1.events.single().payload.duration.value < pass2.events.single().payload.duration.value)

        val finalState = pass3.states.getValue(eventId)
        assertTrue(finalState.isFinal)
        assertEquals(sessionStart + 150_000, finalState.endMillis)
    }

    @Test
    fun unchangedFinalizedSessionsAreNotReEmitted() {
        val eventId = UsageEventIds.forSession(deviceId, installGuid, "tv.danmaku.bili", sessionStart).toString()
        val previous = mapOf(
            eventId to UsageIntervalState(
                packageName = "tv.danmaku.bili",
                startMillis = sessionStart,
                endMillis = sessionStart + 150_000,
                revision = 3L,
                isFinal = true,
            ),
        )

        val result = plan(
            listOf(UsageInterval("tv.danmaku.bili", sessionStart, sessionStart + 150_000)),
            stateView(previous),
            sessionStart + 300_000,
        )

        assertTrue(result.events.isEmpty())
        assertEquals(3L, result.states.getValue(eventId).revision)
    }

    @Test
    fun correctedBoundsOfAFinalizedSessionGetAHigherRevision() {
        val eventId = UsageEventIds.forSession(deviceId, installGuid, "tv.danmaku.bili", sessionStart).toString()
        val previous = mapOf(
            eventId to UsageIntervalState(
                packageName = "tv.danmaku.bili",
                startMillis = sessionStart,
                endMillis = sessionStart + 150_000,
                revision = 3L,
                isFinal = true,
            ),
        )

        val result = plan(
            listOf(UsageInterval("tv.danmaku.bili", sessionStart, sessionStart + 160_000)),
            stateView(previous),
            sessionStart + 300_000,
        )

        val event = result.events.single()
        assertEquals(4L, event.revision)
        assertEquals(FinalizationState.FINAL, event.finalizationState)
        assertEquals(160_000L, event.payload.duration.value)
    }

    @Test
    fun privateObservationsAreBlockedBeforeTheUploadPath() {
        val result = plan(
            listOf(UsageInterval("com.private.bank", sessionStart, sessionStart + 60_000)),
            stateView(),
            sessionStart + 60_000,
            privacyLevelOf = { packageName ->
                if (packageName == "com.private.bank") USAGE_PRIVACY_PRIVATE else USAGE_PRIVACY_NORMAL
            },
        )

        assertTrue(result.events.isEmpty())
        assertTrue(result.states.isEmpty())
        assertEquals(1, result.droppedPrivateCount)
    }

    @Test
    fun sensitiveObservationsStayContractRepresentable() {
        val result = plan(
            listOf(UsageInterval("com.a", sessionStart, sessionStart + 60_000)),
            stateView(),
            sessionStart + 60_000,
            privacyLevelOf = { USAGE_PRIVACY_SENSITIVE },
        )

        assertEquals(PrivacyLevel.SENSITIVE, result.events.single().privacyLevel)
    }

    @Test
    fun zeroLengthAndPastStartingIntervalsAreSkipped() {
        val result = plan(
            listOf(
                UsageInterval("com.a", sessionStart, sessionStart), // zero length
                UsageInterval("com.b", sessionStart, sessionStart - 1_000), // inverted
            ),
            stateView(),
            sessionStart,
        )
        assertTrue(result.events.isEmpty())
        assertTrue(result.states.isEmpty())
    }

    @Test
    fun sessionsAreIdentifiedByPackageAndStartNotByTheirBounds() {
        val result = plan(
            listOf(
                UsageInterval("com.a", sessionStart, sessionStart + 60_000),
                UsageInterval("com.b", sessionStart, sessionStart + 30_000),
            ),
            stateView(),
            sessionStart + 60_000,
        )
        assertEquals(2, result.events.map { it.eventId }.toSet().size)
        assertEquals(
            setOf(
                UsageEventIds.forSession(deviceId, installGuid, "com.a", sessionStart).toString(),
                UsageEventIds.forSession(deviceId, installGuid, "com.b", sessionStart).toString(),
            ),
            result.events.map { it.eventId }.toSet(),
        )
    }
}
