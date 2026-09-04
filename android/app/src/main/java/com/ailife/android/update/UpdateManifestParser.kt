package com.ailife.android.update

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonPrimitive

private val SEMVER = Regex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$")
private val SHA256 = Regex("^[0-9a-f]{64}$")
private val ISO_INSTANT = Regex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d{3})?Z$")
private val COMPONENT = Regex("^[a-z][a-z0-9-]*$")

private val REQUIRED_FIELDS = setOf(
    "manifest_version",
    "component",
    "version",
    "released_at",
    "download_url",
    "sha256",
    "min_compatible_version",
)

/**
 * Core semver X.Y.Z comparison for update decisions: parts compare
 * numerically, never lexically.
 */
fun compareSemver(left: String, right: String): Int {
    val first = parseSemver(left)
    val second = parseSemver(right)
    for (index in 0 until 3) {
        if (first[index] != second[index]) return if (first[index] < second[index]) -1 else 1
    }
    return 0
}

private fun parseSemver(value: String): List<Int> {
    val match = SEMVER.matchEntire(value) ?: throw IllegalArgumentException("Not a core semver version: $value")
    return match.groupValues.drop(1).map { it.toInt() }
}

/**
 * Strict consumer-side parse of one update manifest. Returns null (with the
 * diagnosable errors logged by the caller through evaluateUpdateText) when
 * any field fails validation.
 */
fun parseUpdateManifest(json: String): UpdateManifest? {
    val root = try {
        Json.parseToJsonElement(json)
    } catch (_: Exception) {
        return null
    }
    if (root !is JsonObject) return null
    val keys = root.keys
    val unknown = keys.filterNot { it in REQUIRED_FIELDS }
    val missing = REQUIRED_FIELDS.filterNot { it in keys }
    if (unknown.isNotEmpty() || missing.isNotEmpty()) return null
    // manifest_version must be a JSON number; a quoted "1" is a parse failure,
    // mirroring the Windows and release-tooling parsers.
    val manifestVersionElement = root["manifest_version"]!!
    if (manifestVersionElement !is JsonPrimitive || manifestVersionElement.isString) return null
    return try {
        val manifest = UpdateManifest(
            manifestVersion = root["manifest_version"]!!.jsonPrimitive.content.toInt(),
            component = root["component"]!!.jsonPrimitive.content,
            version = root["version"]!!.jsonPrimitive.content,
            releasedAt = root["released_at"]!!.jsonPrimitive.content,
            downloadUrl = root["download_url"]!!.jsonPrimitive.content,
            sha256 = root["sha256"]!!.jsonPrimitive.content,
            minCompatibleVersion = root["min_compatible_version"]!!.jsonPrimitive.content,
        )
        if (isValidManifest(manifest)) manifest else null
    } catch (_: Exception) {
        null
    }
}

private fun isValidManifest(manifest: UpdateManifest): Boolean {
    if (manifest.manifestVersion != 1) return false
    if (!COMPONENT.matches(manifest.component)) return false
    if (!SEMVER.matches(manifest.version)) return false
    if (!SEMVER.matches(manifest.minCompatibleVersion)) return false
    if (!ISO_INSTANT.matches(manifest.releasedAt)) return false
    if (!SHA256.matches(manifest.sha256)) return false
    val url = runCatching { java.net.URI(manifest.downloadUrl) }.getOrNull() ?: return false
    if (url.scheme != "https" && url.scheme != "http") return false
    if (compareSemver(manifest.minCompatibleVersion, manifest.version) > 0) return false
    return true
}

/**
 * Decides whether one parsed manifest describes an update this client should
 * offer. A manifest published for another component is never an update for
 * this one, so independent component releases can never make a different
 * client report an update.
 */
fun evaluateUpdate(component: String, currentVersion: String, manifest: UpdateManifest): UpdateDecision {
    if (manifest.component != component) {
        return UpdateDecision(
            kind = UpdateDecisionKind.REFUSE,
            code = UpdateCodes.MANIFEST_COMPONENT_MISMATCH,
            detail = "the manifest is for component \"${manifest.component}\", not \"$component\"",
        )
    }
    if (compareSemver(manifest.version, currentVersion) <= 0) {
        return UpdateDecision(UpdateDecisionKind.UP_TO_DATE, UpdateCodes.MANIFEST_VERSION_NOT_NEWER)
    }
    if (compareSemver(currentVersion, manifest.minCompatibleVersion) < 0) {
        return UpdateDecision(
            kind = UpdateDecisionKind.REFUSE,
            code = UpdateCodes.MIN_COMPATIBLE_NOT_MET,
            version = manifest.version,
            releasedAt = manifest.releasedAt,
            downloadUrl = manifest.downloadUrl,
            sha256 = manifest.sha256,
            minCompatibleVersion = manifest.minCompatibleVersion,
            detail = "running $currentVersion predates min_compatible_version ${manifest.minCompatibleVersion}",
        )
    }
    return UpdateDecision(
        kind = UpdateDecisionKind.AVAILABLE,
        code = UpdateCodes.UPDATE_AVAILABLE,
        version = manifest.version,
        releasedAt = manifest.releasedAt,
        downloadUrl = manifest.downloadUrl,
        sha256 = manifest.sha256,
        minCompatibleVersion = manifest.minCompatibleVersion,
    )
}

/** Parses then evaluates in one step; unparseable manifests refuse with manifest_parse_failed. */
fun evaluateUpdateText(component: String, currentVersion: String, json: String): UpdateDecision {
    val manifest = parseUpdateManifest(json) ?: return UpdateDecision(
        kind = UpdateDecisionKind.REFUSE,
        code = UpdateCodes.MANIFEST_PARSE_FAILED,
        detail = "the update manifest is missing, malformed, or carries unknown fields",
    )
    return evaluateUpdate(component, currentVersion, manifest)
}
