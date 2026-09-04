package com.ailife.android.update

/**
 * Component release channels (ADR-0002): each component publishes its own
 * update manifest as a Release Asset, and a client only ever evaluates the
 * manifest of its own component. These codes and decision semantics are
 * mirrored by scripts/release/manifest.mjs and the Windows UpdateEvaluator;
 * the known-vector tests on all three pin identical decisions.
 */
object UpdateComponents {
    const val WINDOWS = "windows"
    const val ANDROID = "android"
}

/** Stable reason codes surfaced by the update check; diagnosable, never free-form exceptions. */
object UpdateCodes {
    const val UPDATE_AVAILABLE = "update_available"
    const val MANIFEST_VERSION_NOT_NEWER = "manifest_version_not_newer"
    const val MANIFEST_PARSE_FAILED = "manifest_parse_failed"
    const val MANIFEST_COMPONENT_MISMATCH = "manifest_component_mismatch"
    const val MIN_COMPATIBLE_NOT_MET = "min_compatible_not_met"
    const val MANIFEST_FETCH_FAILED = "manifest_fetch_failed"
}

enum class UpdateCheckState {
    IDLE,
    UP_TO_DATE,
    AVAILABLE,
    INCOMPATIBLE,
    FAILED,
}

/**
 * One component release's update manifest (manifest_version 1). Unknown
 * fields are rejected on parse so the manifest has no place to hide
 * unvalidated content.
 */
data class UpdateManifest(
    val manifestVersion: Int,
    val component: String,
    val version: String,
    val releasedAt: String,
    val downloadUrl: String,
    val sha256: String,
    val minCompatibleVersion: String,
)

data class UpdateDecision(
    val kind: UpdateDecisionKind,
    val code: String,
    val version: String? = null,
    val releasedAt: String? = null,
    val downloadUrl: String? = null,
    val sha256: String? = null,
    val minCompatibleVersion: String? = null,
    val detail: String? = null,
)

enum class UpdateDecisionKind {
    AVAILABLE,
    UP_TO_DATE,
    REFUSE,
}

/** The persisted result of one update check, shown on the status screen. */
data class UpdateCheckSnapshot(
    val state: UpdateCheckState,
    val availableVersion: String? = null,
    val releasedAt: String? = null,
    val downloadUrl: String? = null,
    val lastCheckAtMillis: Long? = null,
    val errorCode: String? = null,
    val errorMessage: String? = null,
)
