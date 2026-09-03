package com.ailife.android.service

import android.accessibilityservice.AccessibilityService
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityEvent
import com.ailife.android.data.SettingsStore
import com.ailife.android.generated.HeartbeatActivity
import com.ailife.android.generated.HeartbeatRequest
import com.ailife.android.generated.Platform
import com.ailife.android.network.ReportClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import java.time.Instant

/**
 * Observes the foreground application and publishes it as the device
 * current-state heartbeat. Window changes send immediately (throttled); a
 * timer re-asserts the last known state so an unchanged foreground still
 * counts as reporting. Only the package name and its display label leave the
 * device; raw accessibility text never enters a heartbeat.
 */
class ForegroundAccessibilityService : AccessibilityService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val tickerHandler = Handler(Looper.getMainLooper())
    private var lastPackageName: String? = null
    private var lastSentAtMillis: Long = 0L

    private val reassertLastState = object : Runnable {
        override fun run() {
            val ageMillis = System.currentTimeMillis() - lastSentAtMillis
            lastPackageName?.takeIf { ageMillis >= REASSERT_INTERVAL_MS }?.let(::sendHeartbeat)
            tickerHandler.postDelayed(this, REASSERT_INTERVAL_MS)
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        tickerHandler.postDelayed(reassertLastState, REASSERT_INTERVAL_MS)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event?.eventType != AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) return
        val packageName = event.packageName?.toString()?.takeIf { it.isNotBlank() } ?: return
        val now = System.currentTimeMillis()
        if (packageName == lastPackageName && now - lastSentAtMillis < HEARTBEAT_INTERVAL_MS) return

        lastPackageName = packageName
        sendHeartbeat(packageName)
    }

    override fun onInterrupt() {
    }

    override fun onDestroy() {
        tickerHandler.removeCallbacks(reassertLastState)
        scope.cancel()
        super.onDestroy()
    }

    private fun sendHeartbeat(packageName: String) {
        val settings = SettingsStore(this)
        if (!settings.isReady()) return
        lastSentAtMillis = System.currentTimeMillis()

        val heartbeat = HeartbeatRequest(
            activity = HeartbeatActivity(
                applicationId = packageName,
                applicationLabel = resolveAppName(packageName),
                isAfk = false, // accessibility context has no AFK signal; idle detection is out of scope here
            ),
            capturedAt = Instant.now().toString(),
            deviceName = settings.deviceName,
            platform = Platform.ANDROID,
        )

        scope.launch {
            val drainer = HeartbeatQueueDrainer(this@ForegroundAccessibilityService, settings)
            drainer.enqueue(heartbeat)
            drainer.drainOnce(MAX_HEARTBEATS_PER_DRAIN)
        }
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
        private const val REASSERT_INTERVAL_MS = 20_000L
        private const val MAX_HEARTBEATS_PER_DRAIN = 20
    }
}
