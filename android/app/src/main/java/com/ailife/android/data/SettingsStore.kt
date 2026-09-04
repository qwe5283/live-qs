package com.ailife.android.data

import android.content.Context

class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("ai_life_settings", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = prefs.getString(KEY_SERVER_URL, "http://10.0.2.2:8787") ?: "http://10.0.2.2:8787"
        set(value) = prefs.edit().putString(KEY_SERVER_URL, value.trim().trimEnd('/')).apply()

    var deviceToken: String
        get() = prefs.getString(KEY_DEVICE_TOKEN, "") ?: ""
        set(value) = prefs.edit().putString(KEY_DEVICE_TOKEN, value.trim()).apply()

    var deviceId: String
        get() = prefs.getString(KEY_DEVICE_ID, "phone") ?: "phone"
        set(value) = prefs.edit().putString(KEY_DEVICE_ID, value.trim().ifEmpty { "phone" }).apply()

    /**
     * Owner identity echoed in contract event envelopes; the server rejects
     * events whose owner does not match the credential owner.
     */
    var ownerId: String
        get() = prefs.getString(KEY_OWNER_ID, DEFAULT_OWNER_ID) ?: DEFAULT_OWNER_ID
        set(value) = prefs.edit().putString(KEY_OWNER_ID, value.trim().ifEmpty { DEFAULT_OWNER_ID }).apply()

    var deviceName: String
        get() = prefs.getString(KEY_DEVICE_NAME, android.os.Build.MODEL ?: "Android") ?: "Android"
        set(value) = prefs.edit().putString(KEY_DEVICE_NAME, value.trim().ifEmpty { "Android" }).apply()

    var lastHealthSyncMillis: Long
        get() = prefs.getLong(KEY_LAST_HEALTH_SYNC, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_HEALTH_SYNC, value).apply()

    var lastUsageSyncDay: String
        get() = prefs.getString(KEY_LAST_USAGE_SYNC_DAY, "") ?: ""
        set(value) = prefs.edit().putString(KEY_LAST_USAGE_SYNC_DAY, value).apply()

    /**
     * The android component's own update manifest URL (the android channel
     * release asset); never a repository-wide latest release.
     */
    var updateManifestUrl: String
        get() = prefs.getString(KEY_UPDATE_MANIFEST_URL, DEFAULT_UPDATE_MANIFEST_URL) ?: DEFAULT_UPDATE_MANIFEST_URL
        set(value) = prefs.edit().putString(KEY_UPDATE_MANIFEST_URL, value.trim().ifEmpty { DEFAULT_UPDATE_MANIFEST_URL }).apply()

    /** Whether the app periodically checks the android component update manifest. */
    var updateCheckEnabled: Boolean
        get() = prefs.getBoolean(KEY_UPDATE_CHECK_ENABLED, true)
        set(value) = prefs.edit().putBoolean(KEY_UPDATE_CHECK_ENABLED, value).apply()

    fun isReady(): Boolean = serverUrl.isNotBlank() && deviceToken.isNotBlank()

    companion object {
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_DEVICE_TOKEN = "device_token"
        /** The stable android channel manifest URL served as a GitHub Release Asset. */
        const val DEFAULT_UPDATE_MANIFEST_URL =
            "https://github.com/qwe5283/live-qs/releases/download/android%2Fstable/liveqs-android-update.json"
        private const val KEY_UPDATE_MANIFEST_URL = "update_manifest_url"
        private const val KEY_UPDATE_CHECK_ENABLED = "update_check_enabled"
        private const val KEY_DEVICE_ID = "device_id"
        private const val KEY_DEVICE_NAME = "device_name"
        private const val KEY_OWNER_ID = "owner_id"
        private const val KEY_LAST_HEALTH_SYNC = "last_health_sync"
        private const val KEY_LAST_USAGE_SYNC_DAY = "last_usage_sync_day"
        private const val DEFAULT_OWNER_ID = "local"
    }
}
