package com.ailife.android.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Mirrors the manifest evaluation vectors pinned by the release tooling
 * (scripts/release/manifest.test.mjs) and the Windows UpdateTests, so all
 * three implementations decide identically.
 */
class UpdateEvaluatorTest {
    private val emptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

    private fun manifestJson(
        component: String = "android",
        version: String = "0.2.0",
        minCompatible: String = "0.1.0",
        extra: String = "",
    ): String = """
        {
          "manifest_version": 1,
          "component": "$component",
          "version": "$version",
          "released_at": "2026-09-04T08:00:00Z",
          "download_url": "https://github.com/qwe5283/live-qs/releases/download/$component%2Fv$version/artifact",
          "sha256": "$emptySha256",
          "min_compatible_version": "$minCompatible"$extra
        }
    """.trimIndent()

    @Test
    fun compareSemverOrdersNumericallyNotLexically() {
        assertEquals(-1, compareSemver("0.1.0", "0.2.0"))
        assertEquals(1, compareSemver("0.2.0", "0.1.0"))
        assertEquals(0, compareSemver("1.0.0", "1.0.0"))
        assertEquals(1, compareSemver("0.10.0", "0.9.0"))
        assertEquals(-1, compareSemver("1.2.3", "1.2.10"))
        assertEquals(1, compareSemver("10.0.0", "9.99.99"))
    }

    @Test
    fun parsesAValidManifest() {
        val manifest = parseUpdateManifest(manifestJson())

        assertNotNull(manifest)
        assertEquals(1, manifest!!.manifestVersion)
        assertEquals("android", manifest.component)
        assertEquals("0.2.0", manifest.version)
        assertEquals(emptySha256, manifest.sha256)
        assertEquals("0.1.0", manifest.minCompatibleVersion)
        assertTrue(manifest.downloadUrl.startsWith("https://github.com/"))
    }

    @Test
    fun rejectsInvalidManifestsWithDiagnosableErrors() {
        val invalidCases = listOf(
            manifestJson(version = "0.2"),
            manifestJson(component = "Android"),
            manifestJson(extra = ",\n  \"notes\": \"extra field\""),
            manifestJson().replace("\"manifest_version\": 1", "\"manifest_version\": 2"),
            manifestJson().replace("\"manifest_version\": 1", "\"manifest_version\": \"1\""),
            manifestJson().replace(emptySha256, "not-a-hash"),
            manifestJson().replaceFirst("manifest_version\": 1,", ""),
            "{ not json }",
            "42",
        )
        for (json in invalidCases) {
            val manifest = parseUpdateManifest(json)
            assertNull(json, manifest)
        }
    }

    @Test
    fun offersAStrictlyNewerApplicableRelease() {
        val manifest = parseUpdateManifest(manifestJson())!!

        val decision = evaluateUpdate("android", "0.1.0", manifest)

        assertEquals(UpdateDecisionKind.AVAILABLE, decision.kind)
        assertEquals("0.2.0", decision.version)
        assertEquals(emptySha256, decision.sha256)
        assertNotNull(decision.downloadUrl)
    }

    @Test
    fun neverOffersADowngradeOrReinstall() {
        val manifest = parseUpdateManifest(manifestJson(version = "0.2.0", minCompatible = "0.2.0"))!!

        assertEquals(
            UpdateDecisionKind.UP_TO_DATE,
            evaluateUpdate("android", "0.2.0", manifest).kind,
        )
        assertEquals(
            UpdateDecisionKind.UP_TO_DATE,
            evaluateUpdate("android", "0.3.1", manifest).kind,
        )
    }

    @Test
    fun refusesAManifestPublishedForAnotherComponent() {
        // Isolation property: a windows release must never look like an
        // android update, however new its version is.
        val manifest = parseUpdateManifest(manifestJson(component = "windows", version = "99.0.0"))!!

        val decision = evaluateUpdate("android", "0.1.0", manifest)

        assertEquals(UpdateDecisionKind.REFUSE, decision.kind)
        assertEquals(UpdateCodes.MANIFEST_COMPONENT_MISMATCH, decision.code)
    }

    @Test
    fun refusesWhenTheRunningClientPredatesTheMinimumCompatibleVersion() {
        val manifest = parseUpdateManifest(manifestJson(version = "9.0.0", minCompatible = "1.0.0"))!!

        val decision = evaluateUpdate("android", "0.0.9", manifest)

        assertEquals(UpdateDecisionKind.REFUSE, decision.kind)
        assertEquals(UpdateCodes.MIN_COMPATIBLE_NOT_MET, decision.code)
    }

    @Test
    fun acceptsAClientExactlyAtTheMinimumCompatibleVersion() {
        val manifest = parseUpdateManifest(manifestJson(minCompatible = "0.1.0"))!!

        assertEquals(UpdateDecisionKind.AVAILABLE, evaluateUpdate("android", "0.1.0", manifest).kind)
    }

    @Test
    fun refusedManifestsCarryTheParseFailureCode() {
        val manifest = parseUpdateManifest("not json")

        assertNull(manifest)
        val decision = evaluateUpdateText("android", "0.1.0", "not json")

        assertEquals(UpdateDecisionKind.REFUSE, decision.kind)
        assertEquals(UpdateCodes.MANIFEST_PARSE_FAILED, decision.code)
        assertTrue(decision.detail!!.isNotEmpty())
        assertFalse(decision.code.isEmpty())
    }
}
