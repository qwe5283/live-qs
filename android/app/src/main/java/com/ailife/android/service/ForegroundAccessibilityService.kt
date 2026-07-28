package com.ailife.android.service

import android.accessibilityservice.AccessibilityService
import android.content.pm.PackageManager
import android.view.accessibility.AccessibilityEvent
import com.ailife.android.data.SettingsStore
import com.ailife.android.network.ReportClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class ForegroundAccessibilityService : AccessibilityService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var lastPackageName: String? = null
    private var lastSentAtMillis: Long = 0L

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event?.eventType != AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) return
        val packageName = event.packageName?.toString()?.takeIf { it.isNotBlank() } ?: return
        val now = System.currentTimeMillis()
        if (packageName == lastPackageName && now - lastSentAtMillis < HEARTBEAT_INTERVAL_MS) return

        lastPackageName = packageName
        lastSentAtMillis = now

        val settings = SettingsStore(this)
        if (!settings.isReady()) return

        val appName = resolveAppName(packageName)
        scope.launch {
            val drainer = HeartbeatQueueDrainer(this@ForegroundAccessibilityService, settings)
            drainer.enqueue(
                ReportClient.foregroundHeartbeat(
                    deviceId = settings.deviceId,
                    packageName = packageName,
                    appName = appName,
                    heartbeatIntervalMs = HEARTBEAT_INTERVAL_MS,
                ),
            )
            drainer.drainOnce(MAX_HEARTBEATS_PER_DRAIN)
        }
    }

    override fun onInterrupt() {
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun resolveAppName(packageName: String): String {
        return try {
            val appInfo = packageManager.getApplicationInfo(packageName, 0)
            packageManager.getApplicationLabel(appInfo).toString()
        } catch (_: PackageManager.NameNotFoundException) {
            packageName
        }
    }

    companion object {
        private const val HEARTBEAT_INTERVAL_MS = 5_000L
        private const val MAX_HEARTBEATS_PER_DRAIN = 20
    }
}
