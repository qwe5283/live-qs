package com.ailife.android.identity

import android.content.Context

/**
 * Resolves the collector version reported in event provenance, falling back
 * to 0.0.0 when the package information is unavailable or malformed. Shared
 * by every event domain so provenance stays consistent.
 */
fun resolveCollectorVersion(context: Context): String {
    return try {
        val packageInfo = context.packageManager.getPackageInfo(context.packageName, 0)
        packageInfo.versionName?.takeIf { version ->
            Regex("^[0-9]+\\.[0-9]+\\.[0-9]+").containsMatchIn(version)
        } ?: "0.0.0"
    } catch (_: Exception) {
        "0.0.0"
    }
}
