package com.ailife.android.classification

import com.ailife.android.generated.ClassificationRuleSet
import java.io.File
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/**
 * Durable cache of the Owner's published classification rule set. The last
 * successfully fetched version survives process death and outages so
 * classification keeps executing offline; a failed refresh is never fatal.
 */
class RuleSetCache(private val file: File, private val nowMillis: () -> Long = System::currentTimeMillis) {

    /** How long a cached rule set is trusted before the next refresh attempt. */
    internal val refreshIntervalMillis: Long = 15L * 60 * 1000

    private val json = Json { ignoreUnknownKeys = true }

    /** The cached rule set, or null when nothing was ever fetched. */
    fun load(): ClassificationRuleSet? {
        if (!file.exists()) return null
        return try {
            json.decodeFromString<CachedRuleSet>(file.readText()).ruleSet
        } catch (_: Exception) {
            // A damaged cache degrades to "no rules known", never to a crash.
            null
        }
    }

    /**
     * Refreshes the cache when the fetched version is older than the refresh
     * interval. A transport or shape failure returns the previous cache.
     */
    fun refresh(serverUrl: String, token: String, fetch: (String, String) -> String?): ClassificationRuleSet? {
        val cachedAt = cachedAtMillis()
        if (cachedAt != null && nowMillis() - cachedAt < refreshIntervalMillis) {
            return load()
        }
        val body = try {
            fetch(serverUrl, token)
        } catch (_: Exception) {
            null
        }
        if (body != null) {
            try {
                val parsed = json.decodeFromString<ClassificationRuleSet>(body)
                file.writeText(json.encodeToString(CachedRuleSet(fetchedAtMillis = nowMillis(), ruleSet = parsed)))
            } catch (_: Exception) {
                // Keep the previous cache for an unusable response body.
            }
        }
        return load()
    }

    private fun cachedAtMillis(): Long? {
        if (!file.exists()) return null
        return try {
            json.decodeFromString<CachedRuleSet>(file.readText()).fetchedAtMillis
        } catch (_: Exception) {
            null
        }
    }
}

/** Persisted cache entry: when it was fetched plus the rule set document itself. */
@Serializable
internal data class CachedRuleSet(
    val fetchedAtMillis: Long,
    val ruleSet: ClassificationRuleSet,
)
