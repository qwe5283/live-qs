package com.ailife.android.health

import android.app.AppOpsManager
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Process
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.model.LifeEvent
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter

class UsageStatsCollector(private val context: Context) {
    fun collectYesterdayAndToday(settings: SettingsStore): List<LifeEvent> {
        return collectRecentDays(settings, 2)
    }

    fun collectRecentDays(settings: SettingsStore, days: Int = 7): List<LifeEvent> {
        if (!hasUsageAccess(context)) return emptyList()

        val zone = ZoneId.systemDefault()
        val today = LocalDate.now(zone)
        return (0 until days)
            .asSequence()
            .map { today.minusDays(it.toLong()) }
            .flatMap { collectDay(settings, it, zone).asSequence() }
            .sortedBy { it.startAt }
            .toList()
    }

    private fun collectDay(settings: SettingsStore, day: LocalDate, zone: ZoneId): List<LifeEvent> {
        val usageStatsManager = context.getSystemService(Context.USAGE_STATS_SERVICE) as UsageStatsManager
        val start = day.atStartOfDay(zone).toInstant()
        val dayEnd = day.plusDays(1).atStartOfDay(zone).toInstant()
        val end = minOf(dayEnd, Instant.now())
        if (!end.isAfter(start)) return emptyList()
        val durations = usageStatsManager.queryForegroundDurations(start.toEpochMilli(), end.toEpochMilli())

        return durations.entries
            .asSequence()
            .filter { it.value.durationMillis > 0 }
            .map { (packageName, foreground) ->
                val minutes = foreground.durationMillis / 60_000.0
                LifeEvent(
                    idempotencyKey = "usage-app-daily-${day}-$packageName",
                    bucket = "android:${settings.deviceId}:usage",
                    type = "usage.app_daily",
                    startAt = iso(start),
                    endAt = iso(end),
                    value = minutes,
                    unit = "min",
                    privacyLevel = "normal",
                    data = mapOf(
                        "package_name" to packageName,
                        "app_name" to resolveAppName(packageName),
                        "date" to day.toString(),
                        "foreground_session_count" to foreground.sessionCount,
                        "hourly_minutes" to foreground.hourlyMillis.map { it / 60_000.0 },
                        "source" to "usage_events",
                    ),
                )
            }
            .sortedByDescending { it.value ?: 0.0 }
            .toList()
    }

    private fun UsageStatsManager.queryForegroundDurations(
        dayStartMillis: Long,
        dayEndMillis: Long,
    ): Map<String, ForegroundDuration> {
        val lookbackStartMillis = dayStartMillis - LOOKBACK_WINDOW_MS
        val events = queryEvents(lookbackStartMillis, dayEndMillis)
        val event = UsageEvents.Event()
        val durations = mutableMapOf<String, ForegroundDuration>()
        var activePackage: String? = null
        var activeClassName: String? = null
        var activeSinceMillis: Long = 0L

        fun closeActive(closedAtMillis: Long) {
            val packageName = activePackage ?: return
            val clippedStart = maxOf(activeSinceMillis, dayStartMillis)
            val clippedEnd = minOf(closedAtMillis, dayEndMillis)
            if (clippedEnd > clippedStart) {
                val duration = durations.getOrPut(packageName) { ForegroundDuration() }
                duration.durationMillis += clippedEnd - clippedStart
                duration.addHourlyMillis(clippedStart, clippedEnd, dayStartMillis)
            }
            activePackage = null
            activeClassName = null
            activeSinceMillis = 0L
        }

        while (events.hasNextEvent()) {
            events.getNextEvent(event)
            val packageName = event.packageName?.takeIf { it.isNotBlank() } ?: continue
            val className = event.className?.takeIf { it.isNotBlank() }
            val eventTime = event.timeStamp.coerceIn(lookbackStartMillis, dayEndMillis)
            when (event.eventType) {
                UsageEvents.Event.ACTIVITY_RESUMED -> {
                    if (activePackage != packageName) {
                        closeActive(eventTime)
                        activePackage = packageName
                        activeSinceMillis = eventTime
                        if (eventTime >= dayStartMillis && eventTime < dayEndMillis) {
                            durations.getOrPut(packageName) { ForegroundDuration() }.sessionCount += 1
                        }
                    }
                    activeClassName = className
                }

                UsageEvents.Event.ACTIVITY_PAUSED,
                UsageEvents.Event.ACTIVITY_STOPPED -> {
                    if (activePackage == packageName && (activeClassName == null || className == null || activeClassName == className)) {
                        closeActive(eventTime)
                    }
                }
            }
        }

        closeActive(dayEndMillis)
        return durations
    }

    private fun resolveAppName(packageName: String): String {
        return try {
            val appInfo = context.packageManager.getApplicationInfo(packageName, 0)
            context.packageManager.getApplicationLabel(appInfo).toString()
        } catch (_: PackageManager.NameNotFoundException) {
            packageName
        }
    }

    private data class ForegroundDuration(
        var durationMillis: Long = 0L,
        var sessionCount: Int = 0,
        val hourlyMillis: LongArray = LongArray(24),
    ) {
        fun addHourlyMillis(startMillis: Long, endMillis: Long, dayStartMillis: Long) {
            var cursor = startMillis
            while (cursor < endMillis) {
                val hourIndex = ((cursor - dayStartMillis) / HOUR_MS).toInt().coerceIn(0, 23)
                val nextHour = dayStartMillis + (hourIndex + 1L) * HOUR_MS
                val segmentEnd = minOf(endMillis, nextHour)
                if (segmentEnd > cursor) {
                    hourlyMillis[hourIndex] += segmentEnd - cursor
                }
                cursor = segmentEnd
            }
        }
    }

    companion object {
        private const val LOOKBACK_WINDOW_MS = 12 * 60 * 60 * 1000L
        private const val HOUR_MS = 60 * 60 * 1000L

        fun hasUsageAccess(context: Context): Boolean {
            val appOps = context.getSystemService(Context.APP_OPS_SERVICE) as AppOpsManager
            val mode = appOps.unsafeCheckOpNoThrow(
                AppOpsManager.OPSTR_GET_USAGE_STATS,
                Process.myUid(),
                context.packageName,
            )
            return mode == AppOpsManager.MODE_ALLOWED
        }

        private fun iso(instant: Instant): String = DateTimeFormatter.ISO_INSTANT.format(instant)
    }
}
