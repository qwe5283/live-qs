package com.ailife.android.service

import com.ailife.android.data.SyncDiagnosticsState
import com.ailife.android.data.TransientSyncError
import com.ailife.android.data.queue.ContractEventSpoolQueue
import com.ailife.android.data.queue.ContractSyncFailure
import com.ailife.android.data.queue.ContractSyncFailures
import com.ailife.android.generated.SyncDiagnosticsReport
import com.ailife.android.health.HealthConnectEventPlanner
import com.ailife.android.health.HealthSleepSample
import java.io.File
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Instant
import java.time.ZoneId
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * The diagnostics snapshot the Android collector pushes: counts, timestamps,
 * and stable-code errors aggregated from every domain's durable outbox and
 * failure queue, plus the persisted device-local sync state.
 */
class SyncDiagnosticsReporterTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val captured = mutableListOf<SyncDiagnosticsReport>()
    private val state by lazy { SyncDiagnosticsState(File(temporaryFolder.root, "sync-diagnostics.json")) }
    private val usage by lazy {
        DiagnosticsDomain(
            queue = ContractEventSpoolQueue(File(temporaryFolder.root, "usage-queue.ndjson")),
            failures = ContractSyncFailures(File(temporaryFolder.root, "usage-failures.ndjson")),
        )
    }
    private val health by lazy {
        DiagnosticsDomain(
            queue = ContractEventSpoolQueue(File(temporaryFolder.root, "health-queue.ndjson")),
            failures = ContractSyncFailures(File(temporaryFolder.root, "health-failures.ndjson")),
        )
    }

    /** One contract-valid event per pending outbox entry, built with the production health planner. */
    private fun plannedEvent(recordId: String) =
        HealthConnectEventPlanner.plan(
            samples = listOf(HealthSleepSample(recordId, "com.urbandroid.sleep", 1_754_043_000_000, 1_754_046_200_000)),
            state = emptyMap(),
            deviceId = "phone",
            ownerId = "local",
            installGuid = "guid",
            nowMillis = 1_754_046_300_000,
            collectorVersion = "0.2.0",
            zone = ZoneId.of("Asia/Shanghai"),
        ).events.single()

    /** Writes a failure entry with a controlled timestamp so ordering assertions stay deterministic. */
    private fun recordFailure(domain: DiagnosticsDomain, index: Int, code: String, at: Instant) {
        val line = Json.encodeToString(
            ContractSyncFailure(
                eventId = "00000000-0000-0000-0000-${index.toString().padStart(12, '0')}",
                revision = 1,
                errorCode = code,
                message = "explained $index",
                recordedAt = at.toString(),
            ),
        )
        File(temporaryFolder.root, "${if (domain === usage) "usage" else "health"}-failures.ndjson").appendText("$line\n")
    }

    private fun reporter(push: (SyncDiagnosticsReport) -> Result<Unit> = {
        captured.add(it)
        Result.success(Unit)
    }): SyncDiagnosticsReporter = SyncDiagnosticsReporter(
        state = state,
        domains = listOf(usage, health),
        deviceName = "Test Phone",
        push = push,
    )

    @Test
    fun aggregatesCountsTimestampsAndErrorsAcrossDomains() {
        usage.queue.enqueueAll(listOf(plannedEvent("hc-record-a"), plannedEvent("hc-record-b")))
        health.queue.enqueueAll(listOf(plannedEvent("hc-record-c")))
        recordFailure(usage, index = 1, code = "invalid_event", at = Instant.parse("2026-08-01T10:00:00Z"))
        recordFailure(health, index = 2, code = "insufficient_scope", at = Instant.parse("2026-08-01T09:00:00Z"))
        state.recordCollection(Instant.parse("2026-08-01T12:00:00Z"))
        state.recordSuccessfulUpload(Instant.parse("2026-08-01T11:30:00Z"))
        state.recordTransientError(TransientSyncError("network_error", "无法连接同步服务。", "2026-08-01T11:45:00Z"))

        val result = reporter().pushOnce()

        assertTrue(result.isSuccess)
        val report = captured.single()
        assertEquals(3, report.pendingCount)
        assertEquals(2, report.permanentFailureCount)
        // Newest first: the transient error, then the two permanent failures.
        assertEquals(listOf("network_error", "invalid_event", "insufficient_scope"), report.recentErrors.map { it.code })
        assertEquals("2026-08-01T12:00:00Z", report.collectedAt)
        assertEquals("2026-08-01T11:30:00Z", report.lastSuccessfulUploadAt)
        // The oldest pending entry is the earliest createdAt among the queued events.
        val queuedCreatedAts = listOf(usage, health).flatMap { it.queue.readAll() }.map { Instant.parse(it.createdAt) }
        assertEquals(queuedCreatedAts.minOrNull()!!.toString(), report.oldestPendingAt)
        assertEquals("Test Phone", report.deviceName)
    }

    @Test
    fun recentErrorWindowIsCappedAtTheTenNewest() {
        for (index in 0 until 12) {
            recordFailure(usage, index = index, code = "invalid_event", at = Instant.parse("2026-08-01T00:0${index / 10}:0${index % 10}Z"))
        }

        reporter().pushOnce()

        val messages = captured.single().recentErrors.map { it.message }
        assertEquals(10, messages.size)
        assertEquals("explained 11", messages.first()) // newest first
        assertEquals("explained 2", messages.last()) // the two oldest fell out of the window
    }

    @Test
    fun transientFailureSummariesAreStableCodesWithFixedSafeText() {
        val (networkCode, networkMessage) = SyncDiagnosticsReporter.describeTransientFailure(UnknownHostException("host.hidden.example"))
        assertEquals("network_error", networkCode)
        assertEquals("无法连接同步服务。", networkMessage)
        assertTrue(!networkMessage.contains("host.hidden.example"))

        val (timeoutCode, _) = SyncDiagnosticsReporter.describeTransientFailure(SocketTimeoutException("read timed out"))
        assertEquals("request_timeout", timeoutCode)
    }

    @Test
    fun failedPushSurfacesAsFailureSoTheNextCadenceRetries() {
        val result = reporter(push = { Result.failure(IllegalStateException("offline")) }).pushOnce()

        assertTrue(result.isFailure)
        assertTrue(captured.isEmpty())
    }
}
