package com.ailife.android.classification

import com.ailife.android.generated.ClassificationRuleSet
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Golden samples for local classification on Android: package rules map to
 * the same service subject their Windows title-rule twins use, priorities and
 * ties resolve deterministically, no-match stays silent, and version plus
 * confidence travel with the result.
 */
class DeviceClassifierTest {
    private val json = Json { ignoreUnknownKeys = true }

    private val crossPlatformRuleSet = json.decodeFromString<ClassificationRuleSet>(
        """
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
        """.trimIndent(),
    )

    @Test
    fun packageRuleMapsTheBilibiliPackageToTheSharedServiceSubject() {
        val tag = DeviceClassifier.classify(crossPlatformRuleSet, "tv.danmaku.bili")

        assertNotNull(tag)
        assertEquals("svc.bilibili", tag!!.subjectId)
        assertEquals("android.bilibili.package", tag.ruleId)
        assertEquals(1L, tag.ruleVersion)
        assertEquals(1.0, tag.confidence, 0.0)
    }

    @Test
    fun unmatchedPackagesProduceNoSubject() {
        assertNull(DeviceClassifier.classify(crossPlatformRuleSet, "com.example.other"))
        assertNull(DeviceClassifier.classify(null, "tv.danmaku.bili"))
        val empty = json.decodeFromString<ClassificationRuleSet>(
            """
            { "rule_set_version": 0, "updated_at": null, "entities": [], "rules": [] }
            """.trimIndent(),
        )
        assertNull(DeviceClassifier.classify(empty, "tv.danmaku.bili"))
    }

    @Test
    fun priorityDecidesConflictsAndEqualPriorityBreaksTiesByRuleId() {
        val conflicted = json.decodeFromString<ClassificationRuleSet>(
            """
            { "rule_set_version": 2, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "b.rule", "platform": "any", "kind": "application", "pattern": "tv.danmaku.bili",
                "priority": 1, "confidence": 0.5, "subject_entity_id": "svc.other", "dynamic": false,
                "version": 1, "updated_at": null },
              { "rule_id": "a.rule", "platform": "any", "kind": "application", "pattern": "tv.danmaku.bili",
                "priority": 10, "confidence": 0.5, "subject_entity_id": "svc.bilibili", "dynamic": false,
                "version": 1, "updated_at": null } ] }
            """.trimIndent(),
        )
        assertEquals("svc.bilibili", DeviceClassifier.classify(conflicted, "tv.danmaku.bili")!!.subjectId)

        val tie = json.decodeFromString<ClassificationRuleSet>(
            """
            { "rule_set_version": 2, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "zzz.tie", "platform": "any", "kind": "application", "pattern": "tv.danmaku.bili",
                "priority": 3, "confidence": 0.5, "subject_entity_id": "svc.z", "dynamic": false,
                "version": 1, "updated_at": null },
              { "rule_id": "aaa.tie", "platform": "any", "kind": "application", "pattern": "tv.danmaku.bili",
                "priority": 3, "confidence": 0.5, "subject_entity_id": "svc.a", "dynamic": false,
                "version": 1, "updated_at": null } ] }
            """.trimIndent(),
        )
        assertEquals("svc.a", DeviceClassifier.classify(tie, "tv.danmaku.bili")!!.subjectId)
    }

    @Test
    fun windowsScopedRulesNeverApplyOnAndroid() {
        val ruleSet = json.decodeFromString<ClassificationRuleSet>(
            """
            { "rule_set_version": 3, "updated_at": null, "entities": [], "rules": [
              { "rule_id": "edge.bilibili.title", "platform": "windows", "kind": "title_keyword",
                "pattern": "bilibili", "priority": 10, "confidence": 0.8,
                "subject_entity_id": "svc.bilibili", "dynamic": false, "version": 3, "updated_at": null } ] }
            """.trimIndent(),
        )
        assertNull(DeviceClassifier.classify(ruleSet, "tv.danmaku.bili"))
    }
}
