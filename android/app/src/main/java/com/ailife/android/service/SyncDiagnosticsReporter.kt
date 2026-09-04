package com.ailife.android.service

import com.ailife.android.data.SyncDiagnosticsState
import com.ailife.android.data.queue.ContractEventSpoolQueue
import com.ailife.android.data.queue.ContractSyncFailures
import com.ailife.android.generated.Platform
import com.ailife.android.generated.SyncDiagnosticError
import com.ailife.android.generated.SyncDiagnosticsReport
import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Instant

/** One sync domain's local files the diagnostics aggregate over. */
data class DiagnosticsDomain(
    val queue: ContractEventSpoolQueue,
    val failures: ContractSyncFailures,
)

/**
 * Builds and pushes the device sync-state snapshot (POST
 * /api/v1/diagnostics/sync): counts, timestamps, and stable-code errors only.
 * Permanent failures travel with their server-provided stable code and safe
 * contract message; transient failures carry a fixed summary for their code
 * so raw exception text (which can embed local content) never leaves the
 * device. A failed push returns a failure so the sync pass retries on the
 * next cadence.
 */
class SyncDiagnosticsReporter(
    private val state: SyncDiagnosticsState,
    private val domains: List<DiagnosticsDomain>,
    private val deviceName: String?,
    private val push: (SyncDiagnosticsReport) -> Result<Unit>,
) {
    fun pushOnce(): Result<Unit> {
        val snapshotState = state.read()
        val spooled = domains.flatMap { it.queue.readAll() }
        val oldestPendingAt = spooled
            .mapNotNull { runCatching { Instant.parse(it.createdAt) }.getOrNull() }
            .minOrNull()
        val recentErrors = domains
            .flatMap { it.failures.readAll() }
            .map { SyncDiagnosticError(code = it.errorCode, message = it.message, occurredAt = it.recordedAt) }
            .plus(
                snapshotState.lastTransientError?.let {
                    listOf(SyncDiagnosticError(code = it.code, message = it.message, occurredAt = it.occurredAt))
                }.orEmpty(),
            )
            .sortedByDescending { runCatching { Instant.parse(it.occurredAt) }.getOrDefault(Instant.EPOCH) }
            .take(MAX_RECENT_ERRORS)

        val report = SyncDiagnosticsReport(
            platform = Platform.ANDROID,
            deviceName = deviceName,
            collectedAt = snapshotState.lastCollectionAt,
            lastSuccessfulUploadAt = snapshotState.lastSuccessfulUploadAt,
            oldestPendingAt = oldestPendingAt?.toString(),
            pendingCount = domains.sumOf { it.queue.size().toLong() },
            permanentFailureCount = domains.sumOf { it.failures.size().toLong() },
            recentErrors = recentErrors,
        )
        return push(report)
    }

    companion object {
        /** The contract bounds the recent-error window; the same cap applies at build time. */
        const val MAX_RECENT_ERRORS = 10

        /**
         * Maps a transport failure to a stable code plus a fixed safe summary.
         * Never returns the exception message: it may embed local URLs, file
         * names, or response bodies.
         */
        fun describeTransientFailure(exception: Throwable): Pair<String, String> = when (exception) {
            is SocketTimeoutException -> "request_timeout" to "同步请求超时。"
            is UnknownHostException, is ConnectException, is IOException -> "network_error" to "无法连接同步服务。"
            is IllegalStateException -> "invalid_sync_response" to "同步服务响应不符合契约。"
            else -> "sync_failed" to "同步过程中发生未知错误。"
        }
    }
}
