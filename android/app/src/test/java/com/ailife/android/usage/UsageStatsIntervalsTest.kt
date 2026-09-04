package com.ailife.android.usage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class UsageStatsIntervalsTest {
    private fun resume(packageName: String, at: Long) = UsageTransition(packageName, at, isResume = true)
    private fun pause(packageName: String, at: Long) = UsageTransition(packageName, at, isResume = false)

    @Test
    fun pairsResumeAndPauseIntoOneSession() {
        val intervals = UsageStatsIntervals.build(
            listOf(resume("com.a", 1_000), pause("com.a", 4_000)),
        )
        assertEquals(listOf(UsageInterval("com.a", 1_000, 4_000)), intervals)
    }

    @Test
    fun aPackageSwitchClosesThePreviousSessionAtTheSameInstant() {
        val intervals = UsageStatsIntervals.build(
            listOf(resume("com.a", 1_000), resume("com.b", 3_500), pause("com.b", 5_000)),
        )
        assertEquals(
            listOf(
                UsageInterval("com.a", 1_000, 3_500),
                UsageInterval("com.b", 3_500, 5_000),
            ),
            intervals,
        )
    }

    @Test
    fun sessionStillForegroundAtWindowEndStaysOpen() {
        val intervals = UsageStatsIntervals.build(listOf(resume("com.a", 1_000)))
        assertEquals(listOf(UsageInterval("com.a", 1_000, null)), intervals)
        assertNull(intervals.single().endMillis)
    }

    @Test
    fun duplicateResumeForTheForegroundPackageIsIgnored() {
        val intervals = UsageStatsIntervals.build(
            listOf(resume("com.a", 1_000), resume("com.a", 2_000), pause("com.a", 3_000)),
        )
        assertEquals(listOf(UsageInterval("com.a", 1_000, 3_000)), intervals)
    }

    @Test
    fun pauseWithoutAnOpenSessionIsIgnored() {
        // The session start predates the queried window; an earlier pass
        // already reported it, so the orphan pause must not invent an interval.
        val intervals = UsageStatsIntervals.build(
            listOf(pause("com.a", 2_000), resume("com.b", 3_000), pause("com.b", 4_000)),
        )
        assertEquals(listOf(UsageInterval("com.b", 3_000, 4_000)), intervals)
    }

    @Test
    fun zeroLengthSessionsAreDropped() {
        val intervals = UsageStatsIntervals.build(
            listOf(resume("com.a", 1_000), pause("com.a", 1_000)),
        )
        assertEquals(emptyList<UsageInterval>(), intervals)
    }

    @Test
    fun foregroundTimeIsConservedAcrossPackageSwitches() {
        // Measurement-method invariant for the ±5% accuracy target: switching
        // hands between packages conserves total foreground time exactly —
        // nothing is lost or double counted.
        val intervals = UsageStatsIntervals.build(
            listOf(
                resume("com.a", 0),
                resume("com.b", 30_000),
                resume("com.c", 90_000),
                pause("com.c", 120_000),
            ),
        )
        val total = intervals.sumOf { (it.endMillis ?: 120_000) - it.startMillis }
        assertEquals(120_000L, total)
        assertEquals(
            listOf(
                UsageInterval("com.a", 0, 30_000),
                UsageInterval("com.b", 30_000, 90_000),
                UsageInterval("com.c", 90_000, 120_000),
            ),
            intervals,
        )
    }
}
