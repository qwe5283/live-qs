package com.ailife.android.usage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test
import java.util.UUID

class UsageEventIdsTest {
    // Independent source of truth: Python `uuid.uuid5(uuid.UUID('10eace7c-1a13-4a4c-af9c-5f4a1d2b3c9d'), name)`,
    // the same known vector the Windows collector pins in EventIdTests, so
    // every platform encodes RFC 4122 v5 identically.
    @Test
    fun matchesRfc4122Uuid5KnownVectors() {
        val namespace = UUID.fromString("10eace7c-1a13-4a4c-af9c-5f4a1d2b3c9d")
        assertEquals(
            UUID.fromString("3dff000c-a867-5737-9ba3-00ee97140c98"),
            UsageEventIds.newUuid5(namespace, "liveqs:activity.interval:device-1:install-1:42"),
        )
        assertEquals(
            UUID.fromString("c059eec1-35e5-54c3-94e8-ca80559b4e68"),
            UsageEventIds.newUuid5(namespace, "liveqs:activity.interval:device-1:install-1:43"),
        )
    }

    @Test
    fun sessionIdentityIsStableAndScopedByDeviceInstallPackageAndStart() {
        val first = UsageEventIds.forSession("phone", "install-1", "tv.danmaku.bili", 1_754_043_000_000)
        assertEquals(first, UsageEventIds.forSession("phone", "install-1", "tv.danmaku.bili", 1_754_043_000_000))
        assertNotEquals(first, UsageEventIds.forSession("phone", "install-1", "tv.danmaku.bili", 1_754_043_000_001))
        assertNotEquals(first, UsageEventIds.forSession("phone", "install-1", "com.other.app", 1_754_043_000_000))
        // A different install GUID means a reinstall or data wipe: the identity
        // epoch changes and never collides with already-uploaded history.
        assertNotEquals(first, UsageEventIds.forSession("phone", "install-2", "tv.danmaku.bili", 1_754_043_000_000))
        assertNotEquals(first, UsageEventIds.forSession("tablet", "install-1", "tv.danmaku.bili", 1_754_043_000_000))
    }
}
