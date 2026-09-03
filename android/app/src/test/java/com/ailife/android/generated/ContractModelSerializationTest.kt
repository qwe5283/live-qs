package com.ailife.android.generated

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertTrue
import org.junit.Test

class ContractModelSerializationTest {
    @Test
    fun activityIntervalUsesContractWireNamesAndValues() {
        val model = ActivityIntervalEventV1(
            eventType = EventType.ACTIVITY_INTERVAL,
            payload = Payload(
                applicationId = "tv.danmaku.bili",
                duration = Duration(value = 300_000, unit = DurationUnit.MS),
                isAfk = false,
            ),
            privacyLevel = PrivacyLevel.NORMAL,
            schemaVersion = 1,
            source = Source(kind = SourceKind.ANDROID_ACCESSIBILITY, recordId = "segment-1042"),
            captureOffsetMinutes = 480,
            captureTimezone = "Asia/Shanghai",
            device = Device(id = "android-phone", platform = Platform.ANDROID),
            endAt = "2026-07-28T01:05:00.000Z",
            eventId = "018f62d6-4f34-7c82-9085-57c8af1d7a44",
            finalizationState = FinalizationState.FINAL,
            invalidated = false,
            ownerId = "owner",
            provenance = Provenance(
                collectorVersion = "0.1.0",
                observedAt = "2026-07-28T01:05:01.000Z",
            ),
            revision = 1,
            startAt = "2026-07-28T01:00:00.000Z",
        )

        val json = Json.encodeToString(model)

        assertTrue(json.contains("\"event_type\":\"activity.interval\""))
        assertTrue(json.contains("\"schema_version\":1"))
        assertTrue(json.contains("\"kind\":\"android.accessibility\""))
        assertTrue(json.contains("\"unit\":\"ms\""))
    }

    @Test
    fun heartbeatRequestUsesContractWireNamesAndOmitsDisplayOnlyNulls() {
        val model = HeartbeatRequest(
            activity = HeartbeatActivity(
                applicationId = "tv.danmaku.bili",
                applicationLabel = "BiliBili",
                isAfk = false,
            ),
            capturedAt = "2026-07-28T01:05:00.000Z",
            deviceName = "Pixel 8",
            platform = Platform.ANDROID,
        )

        val json = Json.encodeToString(model)

        assertTrue(json.contains("\"platform\":\"android\""))
        assertTrue(json.contains("\"captured_at\":\"2026-07-28T01:05:00.000Z\""))
        assertTrue(json.contains("\"application_id\":\"tv.danmaku.bili\""))
        assertTrue(json.contains("\"is_afk\":false"))
        assertTrue(json.contains("\"device_name\":\"Pixel 8\""))

        val anonymous = model.copy(deviceName = null, activity = model.activity.copy(applicationLabel = null))
        val minimalJson = Json.encodeToString(anonymous)
        assertTrue(!minimalJson.contains("device_name"))
        assertTrue(!minimalJson.contains("application_label"))
    }
}
