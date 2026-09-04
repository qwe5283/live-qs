package com.ailife.android.update

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
import com.ailife.android.identity.resolveCollectorVersion
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * Periodic component update check on the Android channel. Notify-only: the
 * result lands in the update-check state file and the status screen; a
 * transport failure retries with WorkManager backoff, every other outcome is
 * final until the next periodic pass.
 */
class UpdateCheckWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {
    override suspend fun doWork(): Result {
        val settings = SettingsStore(applicationContext)
        if (!settings.updateCheckEnabled) return Result.success()

        val checker = UpdateChecker(
            stateStore = UpdateCheckStateStore(File(applicationContext.filesDir, UPDATE_STATE_DIRECTORY)),
            fetchManifest = { UpdateChecker.fetchOverHttp(it) },
            currentVersion = { resolveCollectorVersion(applicationContext) },
        )
        return try {
            checker.checkOnce(settings.updateManifestUrl)
            Result.success()
        } catch (_: Exception) {
            // checkOnce records the diagnosable failure itself; a transport
            // blow-up before that still deserves a bounded retry.
            Result.retry()
        }
    }

    companion object {
        const val UPDATE_STATE_DIRECTORY = "update"
        private const val UNIQUE_PERIODIC_WORK = "ai_life_update_check"
        private const val UNIQUE_ONE_TIME_WORK = "ai_life_update_check_now"

        fun schedule(context: Context) {
            val constraints = Constraints.Builder()
                .setRequiredNetworkType(NetworkType.CONNECTED)
                .build()
            val request = PeriodicWorkRequestBuilder<UpdateCheckWorker>(12, TimeUnit.HOURS)
                .setConstraints(constraints)
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 1, TimeUnit.MINUTES)
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                UNIQUE_PERIODIC_WORK,
                ExistingPeriodicWorkPolicy.UPDATE,
                request,
            )
        }

        fun checkNow(context: Context) {
            val request = OneTimeWorkRequestBuilder<UpdateCheckWorker>()
                .setConstraints(
                    Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build(),
                )
                .build()
            WorkManager.getInstance(context).enqueueUniqueWork(
                UNIQUE_ONE_TIME_WORK,
                ExistingWorkPolicy.REPLACE,
                request,
            )
        }
    }
}
