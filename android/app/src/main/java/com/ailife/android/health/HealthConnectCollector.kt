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
import java.time.Instant

/**
 * Reads Health Connect records. The platform permission flow (read
 * permissions, rationale, request contract) is unchanged; only the output
 * moved from the deleted legacy event channel to typed samples that the
 * contract event path (ticket 13) turns into versioned events.
 *
 * Every sample keeps the record's [HealthSample.dataOrigin] — the application
 * that wrote the record into Health Connect — and its stable
 * [HealthSample.recordId] so source record counts reconcile against server
 * acknowledgements.
 */
class HealthConnectCollector(private val context: Context) {
    private val client: HealthConnectClient by lazy { HealthConnectClient.getOrCreate(context) }

    val readPermissions: Set<String> = setOf(
        HealthPermission.getReadPermission(StepsRecord::class),
        HealthPermission.getReadPermission(HeartRateRecord::class),
        HealthPermission.getReadPermission(SleepSessionRecord::class),
    )

    fun permissionContract() = PermissionController.createRequestPermissionResultContract()

    suspend fun grantedPermissions(): Set<String> = client.permissionController.getGrantedPermissions()

    /**
     * Reads the granted record types in [since, until). A type whose read
     * permission is missing contributes nothing: the permission gate stays
     * visible in the UI instead of producing a silent failure here.
     */
    suspend fun readSamples(since: Instant, until: Instant): List<HealthSample> {
        if (!isAvailable(context) || !since.isBefore(until)) return emptyList()

        val granted = grantedPermissions()
        val timeRange = TimeRangeFilter.between(since, until)
        val samples = mutableListOf<HealthSample>()

        if (HealthPermission.getReadPermission(StepsRecord::class) in granted) {
            val response = client.readRecords(ReadRecordsRequest(StepsRecord::class, timeRange))
            for (record in response.records) {
                samples += HealthStepsSample(
                    recordId = record.metadata.id,
                    dataOrigin = record.metadata.dataOrigin.packageName,
                    startMillis = record.startTime.toEpochMilli(),
                    endMillis = record.endTime.toEpochMilli(),
                    count = record.count,
                )
            }
        }

        if (HealthPermission.getReadPermission(HeartRateRecord::class) in granted) {
            val response = client.readRecords(ReadRecordsRequest(HeartRateRecord::class, timeRange))
            for (record in response.records) {
                for (sample in record.samples) {
                    samples += HealthHeartRateSample(
                        recordId = record.metadata.id,
                        dataOrigin = record.metadata.dataOrigin.packageName,
                        startMillis = sample.time.toEpochMilli(),
                        beatsPerMinute = sample.beatsPerMinute,
                    )
                }
            }
        }

        if (HealthPermission.getReadPermission(SleepSessionRecord::class) in granted) {
            val response = client.readRecords(ReadRecordsRequest(SleepSessionRecord::class, timeRange))
            for (record in response.records) {
                samples += HealthSleepSample(
                    recordId = record.metadata.id,
                    dataOrigin = record.metadata.dataOrigin.packageName,
                    startMillis = record.startTime.toEpochMilli(),
                    endMillis = record.endTime.toEpochMilli(),
                )
            }
        }

        return samples
    }

    companion object {
        fun isAvailable(context: Context): Boolean =
            HealthConnectClient.getSdkStatus(context) == HealthConnectClient.SDK_AVAILABLE

        fun stepsReadPermission(): String = HealthPermission.getReadPermission(StepsRecord::class)

        fun heartRateReadPermission(): String = HealthPermission.getReadPermission(HeartRateRecord::class)

        fun sleepReadPermission(): String = HealthPermission.getReadPermission(SleepSessionRecord::class)
    }
}
