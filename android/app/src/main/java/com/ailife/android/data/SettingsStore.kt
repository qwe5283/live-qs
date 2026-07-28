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

    var deviceName: String
        get() = prefs.getString(KEY_DEVICE_NAME, android.os.Build.MODEL ?: "Android") ?: "Android"
        set(value) = prefs.edit().putString(KEY_DEVICE_NAME, value.trim().ifEmpty { "Android" }).apply()

    var lastHealthSyncMillis: Long
        get() = prefs.getLong(KEY_LAST_HEALTH_SYNC, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_HEALTH_SYNC, value).apply()

    var lastUsageSyncDay: String
        get() = prefs.getString(KEY_LAST_USAGE_SYNC_DAY, "") ?: ""
        set(value) = prefs.edit().putString(KEY_LAST_USAGE_SYNC_DAY, value).apply()

    fun isReady(): Boolean = serverUrl.isNotBlank() && deviceToken.isNotBlank()

    companion object {
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_DEVICE_TOKEN = "device_token"
        private const val KEY_DEVICE_ID = "device_id"
        private const val KEY_DEVICE_NAME = "device_name"
        private const val KEY_LAST_HEALTH_SYNC = "last_health_sync"
        private const val KEY_LAST_USAGE_SYNC_DAY = "last_usage_sync_day"
    }
}
