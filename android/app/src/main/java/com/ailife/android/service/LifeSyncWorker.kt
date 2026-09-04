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
import com.ailife.android.data.SyncDiagnosticsState
import com.ailife.android.data.TransientSyncError
import com.ailife.android.data.queue.ContractEventSpoolQueue
import com.ailife.android.data.queue.ContractSyncFailures
import com.ailife.android.health.HealthConnectEventSource
import com.ailife.android.network.ReportClient
import com.ailife.android.usage.UsageStatsEventSource
import java.io.File
import java.time.Instant
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
        val usageCollected = UsageStatsEventSource(applicationContext, settings).collectPendingEvents()
        usageDrainer.enqueue(usageCollected)

        val healthDrainer = ContractEventQueueDrainer(
            applicationContext,
            settings,
            HEALTH_QUEUE,
            HEALTH_FAILURES,
        )
        val healthCollected = HealthConnectEventSource(applicationContext, settings).collectPendingEvents()
        healthDrainer.enqueue(healthCollected)

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

        // Diagnostics state reflects what this pass actually did: a local
        // collection pass happened, at least one revision was acknowledged
        // (accepted, duplicate, or stale — the same semantics as the Windows
        // worker), and the latest transient failure (stable code, safe
        // summary) is recorded.
        val state = SyncDiagnosticsState(File(applicationContext.filesDir, DIAGNOSTICS_STATE))
        state.recordCollection()
        for (result in listOf(usageResult, healthResult, paymentResult)) {
            val counts = result.getOrNull()
            if (counts != null && counts.accepted + counts.duplicates + counts.staleRevisions > 0) {
                state.recordSuccessfulUpload()
            }
        }
        for (result in listOf(heartbeatResult, usageResult, healthResult, paymentResult)) {
            result.exceptionOrNull()?.let { failure ->
                val (code, message) = SyncDiagnosticsReporter.describeTransientFailure(failure)
                state.recordTransientError(
                    TransientSyncError(
                        code = code,
                        message = message,
                        occurredAt = Instant.now().toString(),
                    ),
                )
            }
        }

        // The snapshot push rides the sync cadence so the Owner can watch the
        // queue drain step by step; a failed push retries with the next pass.
        val diagnosticsResult = pushDiagnostics(settings, state)
        return if (heartbeatResult.isSuccess && usageResult.isSuccess && healthResult.isSuccess &&
            paymentResult.isSuccess && diagnosticsResult.isSuccess
        ) {
            Result.success()
        } else {
            Result.retry()
        }
    }

    /** Aggregates every domain's local queue state and pushes one snapshot. */
    private fun pushDiagnostics(settings: SettingsStore, state: SyncDiagnosticsState): kotlin.Result<Unit> {
        val domains = listOf(USAGE_QUEUE to USAGE_FAILURES, HEALTH_QUEUE to HEALTH_FAILURES, PAYMENT_QUEUE to PAYMENT_FAILURES)
            .map { (queueFile, failuresFile) ->
                DiagnosticsDomain(
                    queue = ContractEventSpoolQueue(File(applicationContext.filesDir, queueFile)),
                    failures = ContractSyncFailures(File(applicationContext.filesDir, failuresFile)),
                )
            }
        val reporter = SyncDiagnosticsReporter(
            state = state,
            domains = domains,
            deviceName = settings.deviceId,
        ) { report ->
            val client = ReportClient(settings.serverUrl, settings.deviceToken)
            try {
                client.postSyncDiagnostics(report)
            } finally {
                client.close()
            }
        }
        return reporter.pushOnce()
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
        const val DIAGNOSTICS_STATE = "sync-diagnostics.json"

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
