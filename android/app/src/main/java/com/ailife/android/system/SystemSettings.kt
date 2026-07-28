package com.ailife.android.system

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import android.text.TextUtils
import android.widget.Toast
import com.ailife.android.service.ForegroundAccessibilityService
import com.ailife.android.service.WechatPayNotificationService

fun hasUsageAccess(context: Context): Boolean {
    val appOps = context.getSystemService(Context.APP_OPS_SERVICE) as android.app.AppOpsManager
    val mode = appOps.unsafeCheckOpNoThrow(
        android.app.AppOpsManager.OPSTR_GET_USAGE_STATS,
        android.os.Process.myUid(),
        context.packageName,
    )
    return mode == android.app.AppOpsManager.MODE_ALLOWED
}

fun isForegroundAccessibilityEnabled(context: Context): Boolean {
    val enabledServices = Settings.Secure.getString(
        context.contentResolver,
        Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES,
    ) ?: return false
    val expected = "${context.packageName}/${ForegroundAccessibilityService::class.java.name}"
    val splitter = TextUtils.SimpleStringSplitter(':')
    splitter.setString(enabledServices)
    for (service in splitter) {
        if (service.equals(expected, ignoreCase = true)) return true
    }
    return false
}

fun isNotificationListenerEnabled(context: Context): Boolean {
    val enabledListeners = Settings.Secure.getString(
        context.contentResolver,
        "enabled_notification_listeners",
    ) ?: return false
    val expected = "${context.packageName}/${WechatPayNotificationService::class.java.name}"
    val splitter = TextUtils.SimpleStringSplitter(':')
    splitter.setString(enabledListeners)
    for (listener in splitter) {
        if (listener.equals(expected, ignoreCase = true)) return true
    }
    return false
}

fun isIgnoringBatteryOptimizations(context: Context): Boolean {
    val pm = context.getSystemService(Context.POWER_SERVICE) as? PowerManager
    return pm?.isIgnoringBatteryOptimizations(context.packageName) == true
}

fun openHealthConnectStore(context: Context) {
    runCatching {
        context.startActivity(
            Intent(Intent.ACTION_VIEW).apply {
                data = Uri.parse("https://play.google.com/store/apps/details?id=com.google.android.apps.healthdata")
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            },
        )
    }.onFailure {
        Toast.makeText(context, "无法打开应用商店", Toast.LENGTH_SHORT).show()
    }
}

fun openBatterySettings(context: Context) {
    runCatching {
        context.startActivity(
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                data = Uri.parse("package:${context.packageName}")
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            },
        )
    }.onFailure {
        runCatching {
            context.startActivity(
                Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                },
            )
        }.onFailure {
            Toast.makeText(context, "无法打开电池优化设置", Toast.LENGTH_SHORT).show()
        }
    }
}

fun openXiaomiAutostart(context: Context) {
    runCatching {
        context.startActivity(
            Intent().apply {
                component = android.content.ComponentName(
                    "com.miui.securitycenter",
                    "com.miui.permcenter.autostart.AutoStartManagementActivity",
                )
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            },
        )
    }.onFailure {
        context.startActivity(
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:${context.packageName}")
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            },
        )
    }
}

fun oemTip(manufacturer: String): String? = when {
    manufacturer.contains("xiaomi") || manufacturer.contains("redmi") ->
        "小米/Redmi：设置 -> 应用设置 -> 应用管理 -> AI Life -> 省电策略 -> 无限制，并开启自启动"
    manufacturer.contains("huawei") || manufacturer.contains("honor") ->
        "华为/荣耀：设置 -> 电池 -> 启动管理 -> AI Life -> 手动管理，三个开关全部打开"
    manufacturer.contains("samsung") ->
        "三星：设置 -> 电池 -> 后台使用限制，从深度睡眠列表中移除 AI Life"
    manufacturer.contains("oppo") || manufacturer.contains("realme") || manufacturer.contains("oneplus") ->
        "OPPO/Realme/一加：设置 -> 电池，允许后台运行和自启动"
    manufacturer.contains("vivo") ->
        "vivo：设置 -> 电池 -> 后台功耗管理 -> AI Life -> 允许后台高耗电"
    else -> null
}

fun deviceSupportsNotificationPermission(): Boolean = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
