package com.ailife.android.service

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.ailife.android.data.SettingsStore
import com.ailife.android.health.HealthConnectEventSource
import com.ailife.android.usage.UsageStatsEventSource
import java.util.concurrent.TimeUnit

class LifeSyncWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {
    override suspend fun doWork(): Result {
        val settings = SettingsStore(applicationContext)
        if (!settings.isReady()) return Result.success()

        // UsageStats is the authoritative daily usage source; Health Connect
        // observations are sensitive, origin-attributed health facts; payment
        // transactions are sensitive, notification-extracted spending facts.
        // All three ride the versioned contract protocol through the shared
        // durable outbox: stable identities, monotonic revisions, per-item
        // acknowledgements, and a visible local failure queue for permanent
        // rejections.
        val usageDrainer = ContractEventQueueDrainer(
            applicationContext,
            settings,
            USAGE_QUEUE,
            USAGE_FAILURES,
        )
        usageDrainer.enqueue(UsageStatsEventSource(applicationContext, settings).collectPendingEvents())

        val healthDrainer = ContractEventQueueDrainer(
            applicationContext,
            settings,
            HEALTH_QUEUE,
            HEALTH_FAILURES,
        )
        healthDrainer.enqueue(HealthConnectEventSource(applicationContext, settings).collectPendingEvents())

        // Payment events are enqueued by the notification listener; the
        // periodic pass retries anything a transport failure left in the
        // outbox.
        val paymentDrainer = ContractEventQueueDrainer(
            applicationContext,
            settings,
            PAYMENT_QUEUE,
            PAYMENT_FAILURES,
        )

        val heartbeatResult = HeartbeatQueueDrainer(applicationContext, settings).drainOnce(MAX_HEARTBEATS_PER_SYNC)
        val usageResult = usageDrainer.drainOnce(MAX_EVENTS_PER_SYNC)
        val healthResult = healthDrainer.drainOnce(MAX_EVENTS_PER_SYNC)
        val paymentResult = paymentDrainer.drainOnce(MAX_EVENTS_PER_SYNC)
        return if (heartbeatResult.isSuccess && usageResult.isSuccess && healthResult.isSuccess && paymentResult.isSuccess) {
            Result.success()
        } else {
            Result.retry()
        }
    }

    companion object {
        private const val UNIQUE_PERIODIC_WORK = "ai_life_sync"
        private const val UNIQUE_ONE_TIME_WORK = "ai_life_sync_now"
        private const val MAX_EVENTS_PER_SYNC = 500
        private const val MAX_HEARTBEATS_PER_SYNC = 100
        /** Outbox and failure-queue file names, shared with the sync/status screens for queue visibility. */
        const val USAGE_QUEUE = "usage-events.ndjson"
        const val USAGE_FAILURES = "usage-sync-failures.ndjson"
        const val HEALTH_QUEUE = "health-events.ndjson"
        const val HEALTH_FAILURES = "health-sync-failures.ndjson"
        const val PAYMENT_QUEUE = WechatPayNotificationService.PAYMENT_QUEUE
        const val PAYMENT_FAILURES = WechatPayNotificationService.PAYMENT_FAILURES

        fun schedule(context: Context) {
            val constraints = Constraints.Builder()
                .setRequiredNetworkType(NetworkType.CONNECTED)
                .build()

            val request = PeriodicWorkRequestBuilder<LifeSyncWorker>(15, TimeUnit.MINUTES)
                .setConstraints(constraints)
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 1, TimeUnit.MINUTES)
                .build()

            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                UNIQUE_PERIODIC_WORK,
                ExistingPeriodicWorkPolicy.UPDATE,
                request,
            )
        }

        fun syncNow(context: Context) {
            val request = OneTimeWorkRequestBuilder<LifeSyncWorker>()
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build()

            WorkManager.getInstance(context).enqueueUniqueWork(
                UNIQUE_ONE_TIME_WORK,
                ExistingWorkPolicy.REPLACE,
                request,
            )
        }
    }
}
