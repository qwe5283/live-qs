package com.ailife.android.update

import java.io.File
import java.io.IOException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * The update check fetches only the android component's own manifest, and
 * every outcome (including refusals) is persisted so the status screen and a
 * restarted process show the same diagnosable state.
 */
class UpdateCheckerTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val emptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

    private fun manifestJson(
        component: String = "android",
        version: String = "0.2.0",
        minCompatible: String = "0.1.0",
    ): String = """
        {
          "manifest_version": 1,
          "component": "$component",
          "version": "$version",
          "released_at": "2026-09-04T08:00:00Z",
          "download_url": "https://github.com/qwe5283/live-qs/releases/download/$component%2Fv$version/LiveQs.Android-$version.apk",
          "sha256": "$emptySha256",
          "min_compatible_version": "$minCompatible"
        }
    """.trimIndent()

    private fun checker(
        fetch: suspend (String) -> String,
        currentVersion: String = "0.1.0",
    ): UpdateChecker {
        val store = UpdateCheckStateStore(temporaryFolder.root)
        return UpdateChecker(
            stateStore = store,
            fetchManifest = fetch,
            currentVersion = { currentVersion },
            nowMillis = { 1_788_600_000_000 },
        )
    }

    @Test
    fun anApplicableNewerReleaseIsReportedAndPersisted() = runBlocking {
        val checker = checker(fetch = { manifestJson() })

        val state = checker.checkOnce("https://example.com/liveqs-android-update.json")

        assertEquals(UpdateCheckState.AVAILABLE, state.state)
        assertEquals("0.2.0", state.availableVersion)
        assertNotNull(state.downloadUrl)
        assertEquals(1_788_600_000_000L, state.lastCheckAtMillis)
        // A restarted process reading the same state file sees the same facts.
        assertEquals(UpdateCheckState.AVAILABLE, UpdateCheckStateStore(temporaryFolder.root).read().state)
    }

    @Test
    fun aTransportFailureIsRecordedWithAStableCode() = runBlocking {
        val checker = checker(fetch = { throw IOException("connection reset") })

        val state = checker.checkOnce("https://example.com/liveqs-android-update.json")

        assertEquals(UpdateCheckState.FAILED, state.state)
        assertEquals(UpdateCodes.MANIFEST_FETCH_FAILED, state.errorCode)
        assertNull(state.availableVersion)
    }

    @Test
    fun anInvalidManifestIsRefusedWithoutOfferingAnUpdate() = runBlocking {
        val checker = checker(fetch = { "not json at all" })

        val state = checker.checkOnce("https://example.com/liveqs-android-update.json")

        assertEquals(UpdateCheckState.FAILED, state.state)
        assertEquals(UpdateCodes.MANIFEST_PARSE_FAILED, state.errorCode)
    }

    @Test
    fun anotherComponentsManifestIsNeverAnUpdateForThisClient() = runBlocking {
        // Isolation property: publishing windows/v9.0.0 must never make the
        // Android client report an update.
        val checker = checker(fetch = { manifestJson(component = "windows", version = "9.0.0") })

        val state = checker.checkOnce("https://example.com/liveqs-android-update.json")

        assertEquals(UpdateCheckState.FAILED, state.state)
        assertEquals(UpdateCodes.MANIFEST_COMPONENT_MISMATCH, state.errorCode)
    }

    @Test
    fun aClientOlderThanMinCompatibleIsRefused() = runBlocking {
        val checker = checker(fetch = { manifestJson(version = "9.0.0", minCompatible = "1.0.0") })

        val state = checker.checkOnce("https://example.com/liveqs-android-update.json")

        assertEquals(UpdateCheckState.INCOMPATIBLE, state.state)
        assertEquals(UpdateCodes.MIN_COMPATIBLE_NOT_MET, state.errorCode)
        assertEquals("9.0.0", state.availableVersion)
    }

    @Test
    fun theCurrentVersionReportsUpToDateWithoutAnOffer() = runBlocking {
        val checker = checker(fetch = { manifestJson(version = "0.1.0", minCompatible = "0.1.0") })

        val state = checker.checkOnce("https://example.com/liveqs-android-update.json")

        assertEquals(UpdateCheckState.UP_TO_DATE, state.state)
        assertNull(state.availableVersion)
    }

    @Test
    fun theStateFileSurvivesCorruptionAsAnEmptyState() {
        val file = File(temporaryFolder.root, "update-check-state.json")
        file.writeText("{ corrupted")

        val state = UpdateCheckStateStore(temporaryFolder.root).read()

        assertEquals(UpdateCheckState.IDLE, state.state)
        assertNull(state.lastCheckAtMillis)
    }
}
