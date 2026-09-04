package com.ailife.android.classification

import com.ailife.android.generated.ClassificationRule
import com.ailife.android.generated.ClassificationRuleKind
import com.ailife.android.generated.ClassificationRulePlatform
import com.ailife.android.generated.ClassificationRuleSet

/**
 * The explainable result of one local match: exactly the fields an uploaded
 * classification carries. The raw local context that produced it never
 * leaves the device.
 */
data class SubjectTag(
    val subjectId: String,
    val ruleId: String,
    val ruleVersion: Long,
    val confidence: Double,
)

/**
 * Executes the Owner's versioned rule set entirely on-device. Android usage
 * observations carry package names instead of window titles, so only
 * application rules are executable here; title rules are Windows concerns.
 * Rules are evaluated in distribution order (priority descending, then
 * rule_id ascending) and the first match wins, so priorities and ties resolve
 * deterministically. When nothing matches, no subject is reported — nothing
 * is guessed and nothing about the local context is uploaded.
 */
object DeviceClassifier {
    fun classify(ruleSet: ClassificationRuleSet?, packageName: String): SubjectTag? {
        val rules = ruleSet?.rules ?: return null
        for (rule in rules.sortedWith(compareByDescending<ClassificationRule> { it.priority }.thenBy { it.ruleId })) {
            if (!appliesTo(rule)) continue
            if (rule.kind != ClassificationRuleKind.APPLICATION) continue
            if (!packageName.equals(rule.pattern, ignoreCase = true)) continue
            val subject = rule.subjectEntityId ?: continue
            return SubjectTag(
                subjectId = subject,
                ruleId = rule.ruleId,
                ruleVersion = rule.version,
                confidence = rule.confidence,
            )
        }
        return null
    }

    private fun appliesTo(rule: ClassificationRule): Boolean = when (rule.platform) {
        ClassificationRulePlatform.ANDROID -> true
        ClassificationRulePlatform.CLASSIFICATION_RULE_PLATFORM_ANY -> true
        ClassificationRulePlatform.WINDOWS -> false
    }
}
