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
import com.ailife.android.data.model.LifeEvent
import com.ailife.android.health.HealthConnectCollector
import com.ailife.android.health.UsageStatsCollector
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.concurrent.TimeUnit

class LifeSyncWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {
    override suspend fun doWork(): Result {
        val settings = SettingsStore(applicationContext)
        if (!settings.isReady()) return Result.success()

        val now = Instant.now()
        val lastHealthSync = settings.lastHealthSyncMillis
        val healthSince = if (lastHealthSync <= 0) {
            now.minus(7, ChronoUnit.DAYS)
        } else {
            Instant.ofEpochMilli(lastHealthSync).minus(5, ChronoUnit.MINUTES)
        }

        val events = mutableListOf<LifeEvent>()
        events += HealthConnectCollector(applicationContext).collect(settings, healthSince, now)
        events += UsageStatsCollector(applicationContext).collectYesterdayAndToday(settings)

        val drainer = EventQueueDrainer(applicationContext, settings)
        if (events.isNotEmpty()) {
            drainer.enqueue(events)
            settings.lastHealthSyncMillis = now.toEpochMilli()
        } else {
            settings.lastHealthSyncMillis = now.toEpochMilli()
        }

        val heartbeatResult = HeartbeatQueueDrainer(applicationContext, settings).drainOnce(MAX_HEARTBEATS_PER_SYNC)
        val eventResult = drainer.drainOnce(MAX_EVENTS_PER_SYNC)
        return if (heartbeatResult.isSuccess && eventResult.isSuccess) Result.success() else Result.retry()
    }

    companion object {
        private const val UNIQUE_PERIODIC_WORK = "ai_life_sync"
        private const val UNIQUE_ONE_TIME_WORK = "ai_life_sync_now"
        private const val MAX_EVENTS_PER_SYNC = 500
        private const val MAX_HEARTBEATS_PER_SYNC = 100

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
