package com.ailife.android.data

import java.io.File
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * The device-local sync facts survive process restarts: a snapshot pushed by
 * a fresh process reports the same state the previous process recorded.
 */
class SyncDiagnosticsStateTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun stateFile() = File(temporaryFolder.root, "sync-diagnostics.json")

    @Test
    fun startsEmptyAndRecordsCollectionUploadAndTransientError() {
        val state = SyncDiagnosticsState(stateFile())

        val empty = state.read()
        assertNull(empty.lastCollectionAt)
        assertNull(empty.lastSuccessfulUploadAt)
        assertNull(empty.lastTransientError)

        state.recordCollection(Instant.parse("2026-08-01T10:00:00Z"))
        state.recordSuccessfulUpload(Instant.parse("2026-08-01T10:05:00Z"))
        state.recordTransientError(TransientSyncError("network_error", "无法连接同步服务。", "2026-08-01T10:06:00Z"))

        val recorded = state.read()
        assertEquals("2026-08-01T10:00:00Z", recorded.lastCollectionAt)
        assertEquals("2026-08-01T10:05:00Z", recorded.lastSuccessfulUploadAt)
        assertEquals("network_error", recorded.lastTransientError?.code)
    }

    @Test
    fun stateSurvivesAProcessRestartOverTheSameFile() {
        SyncDiagnosticsState(stateFile()).apply {
            recordCollection(Instant.parse("2026-08-01T10:00:00Z"))
            recordTransientError(TransientSyncError("request_timeout", "同步请求超时。", "2026-08-01T10:06:00Z"))
        }

        // A fresh process instance over the same file sees the same facts.
        val restarted = SyncDiagnosticsState(stateFile()).read()
        assertEquals("2026-08-01T10:00:00Z", restarted.lastCollectionAt)
        assertNull(restarted.lastSuccessfulUploadAt)
        assertEquals("request_timeout", restarted.lastTransientError?.code)
    }

    @Test
    fun corruptStateFileFallsBackToEmptyInsteadOfBreakingTheSyncPass() {
        stateFile().writeText("{not json")

        val state = SyncDiagnosticsState(stateFile()).read()

        assertNull(state.lastCollectionAt)
        assertNull(state.lastTransientError)
    }
}
