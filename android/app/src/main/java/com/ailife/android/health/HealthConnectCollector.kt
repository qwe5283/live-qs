package com.ailife.android.health

import android.content.Context
import androidx.health.connect.client.HealthConnectClient
import androidx.health.connect.client.PermissionController
import androidx.health.connect.client.permission.HealthPermission
import androidx.health.connect.client.records.HeartRateRecord
import androidx.health.connect.client.records.SleepSessionRecord
import androidx.health.connect.client.records.StepsRecord
import androidx.health.connect.client.request.ReadRecordsRequest
import androidx.health.connect.client.time.TimeRangeFilter
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.model.LifeEvent
import java.time.Duration
import java.time.Instant
import java.time.format.DateTimeFormatter

class HealthConnectCollector(private val context: Context) {
    private val client: HealthConnectClient by lazy { HealthConnectClient.getOrCreate(context) }

    val readPermissions: Set<String> = setOf(
        HealthPermission.getReadPermission(StepsRecord::class),
        HealthPermission.getReadPermission(HeartRateRecord::class),
        HealthPermission.getReadPermission(SleepSessionRecord::class),
    )

    fun permissionContract() = PermissionController.createRequestPermissionResultContract()

    suspend fun grantedPermissions(): Set<String> = client.permissionController.getGrantedPermissions()

    suspend fun collect(settings: SettingsStore, since: Instant, until: Instant): List<LifeEvent> {
        if (!isAvailable(context) || !since.isBefore(until)) return emptyList()

        val granted = grantedPermissions()
        val timeRange = TimeRangeFilter.between(since, until)
        val events = mutableListOf<LifeEvent>()
        val bucket = "android:${settings.deviceId}:health"

        if (HealthPermission.getReadPermission(StepsRecord::class) in granted) {
            val response = client.readRecords(ReadRecordsRequest(StepsRecord::class, timeRange))
            for (record in response.records) {
                events += LifeEvent(
                    idempotencyKey = "health-steps-${record.startTime}-${record.endTime}",
                    bucket = bucket,
                    type = "health.steps",
                    startAt = iso(record.startTime),
                    endAt = iso(record.endTime),
                    value = record.count.toDouble(),
                    unit = "count",
                    privacyLevel = "sensitive",
                )
            }
        }

        if (HealthPermission.getReadPermission(HeartRateRecord::class) in granted) {
            val response = client.readRecords(ReadRecordsRequest(HeartRateRecord::class, timeRange))
            for (record in response.records) {
                for (sample in record.samples) {
                    events += LifeEvent(
                        idempotencyKey = "health-heart-rate-${sample.time}-${sample.beatsPerMinute}",
                        bucket = bucket,
                        type = "health.heart_rate",
                        startAt = iso(sample.time),
                        value = sample.beatsPerMinute.toDouble(),
                        unit = "bpm",
                        privacyLevel = "sensitive",
                    )
                }
            }
        }

        if (HealthPermission.getReadPermission(SleepSessionRecord::class) in granted) {
            val response = client.readRecords(ReadRecordsRequest(SleepSessionRecord::class, timeRange))
            for (record in response.records) {
                events += LifeEvent(
                    idempotencyKey = "health-sleep-${record.startTime}-${record.endTime}",
                    bucket = bucket,
                    type = "health.sleep",
                    startAt = iso(record.startTime),
                    endAt = iso(record.endTime),
                    value = Duration.between(record.startTime, record.endTime).toMinutes().toDouble(),
                    unit = "min",
                    privacyLevel = "sensitive",
                )
            }
        }

        return events
    }

    companion object {
        fun isAvailable(context: Context): Boolean =
            HealthConnectClient.getSdkStatus(context) == HealthConnectClient.SDK_AVAILABLE

        fun stepsReadPermission(): String = HealthPermission.getReadPermission(StepsRecord::class)

        fun heartRateReadPermission(): String = HealthPermission.getReadPermission(HeartRateRecord::class)

        fun sleepReadPermission(): String = HealthPermission.getReadPermission(SleepSessionRecord::class)

        private fun iso(instant: Instant): String = DateTimeFormatter.ISO_INSTANT.format(instant)
    }
}
