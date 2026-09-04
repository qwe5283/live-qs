package com.ailife.android.classification

import java.io.File
import java.nio.file.Files
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The cached rule set survives restarts and outages so offline classification keeps working. */
class RuleSetCacheTest {
    private val ruleSetJson = """
        {
          "rule_set_version": 7,
          "updated_at": "2026-08-21T02:00:00.000Z",
          "entities": [
            { "entity_id": "svc.bilibili", "kind": "service", "name": "哔哩哔哩" }
          ],
          "rules": [
            { "rule_id": "android.bilibili.package", "platform": "any", "kind": "application",
              "pattern": "tv.danmaku.bili", "priority": 0, "confidence": 1,
              "subject_entity_id": "svc.bilibili", "dynamic": false, "version": 1,
              "updated_at": "2026-08-21T02:00:00.000Z" }
          ]
        }
    """.trimIndent()

    private fun newCache(nowMillis: () -> Long): Pair<RuleSetCache, File> {
        val file = File(Files.createTempDirectory("ruleset-cache").toFile(), "classification-rules.json")
        return RuleSetCache(file, nowMillis) to file
    }

    @Test
    fun refreshStoresTheFetchedVersionAndSubsequentLoadsReadItBack() {
        var now = 1_000_000L
        val (cache, _) = newCache({ now })

        val fetched = cache.refresh("http://server", "token") { _, _ -> ruleSetJson }

        assertNotNull(fetched)
        assertEquals(7L, fetched!!.ruleSetVersion)
        assertEquals(7L, cache.load()!!.ruleSetVersion)
        assertEquals("svc.bilibili", DeviceClassifier.classify(cache.load(), "tv.danmaku.bili")!!.subjectId)
    }

    @Test
    fun aFailedRefreshKeepsTheLastSuccessfulVersion() {
        var now = 1_000_000L
        val (cache, _) = newCache({ now })
        cache.refresh("http://server", "token") { _, _ -> ruleSetJson }

        now += cache.refreshIntervalMillis + 1
        val duringOutage = cache.refresh("http://server", "token") { _, _ -> throw java.io.IOException("offline") }

        assertNotNull(duringOutage)
        assertEquals(7L, duringOutage!!.ruleSetVersion)
    }

    @Test
    fun refreshInsideTheIntervalDoesNotFetchAgain() {
        var now = 1_000_000L
        val (cache, _) = newCache({ now })
        var fetchCount = 0

        cache.refresh("http://server", "token") { _, _ -> fetchCount++; ruleSetJson }
        now += 60_000
        val reused = cache.refresh("http://server", "token") { _, _ -> fetchCount++; ruleSetJson }

        assertEquals(1, fetchCount)
        assertEquals(7L, reused!!.ruleSetVersion)
    }

    @Test
    fun anUnusableResponseKeepsThePreviousCache() {
        var now = 1_000_000L
        val (cache, _) = newCache({ now })
        cache.refresh("http://server", "token") { _, _ -> ruleSetJson }

        now += cache.refreshIntervalMillis + 1
        cache.refresh("http://server", "token") { _, _ -> "not json at all" }

        assertEquals(7L, cache.load()!!.ruleSetVersion)
    }

    @Test
    fun aDamagedCacheFileDegradesToNoRulesInsteadOfCrashing() {
        var now = 1_000_000L
        val (cache, file) = newCache({ now })
        cache.refresh("http://server", "token") { _, _ -> ruleSetJson }

        now += cache.refreshIntervalMillis + 1
        file.writeText("{ broken")

        assertNull(cache.load())
        assertTrue(file.exists())
    }
}
