package com.ailife.android.ui.screens

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.health.connect.client.PermissionController
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.repeatOnLifecycle
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.model.LifeEvent
import com.ailife.android.health.HealthConnectCollector
import com.ailife.android.health.HealthSample
import com.ailife.android.health.HealthSampleKind
import com.ailife.android.health.HealthHeartRateSample
import com.ailife.android.health.HealthSleepSample
import com.ailife.android.health.HealthStepsSample
import com.ailife.android.health.UsageStatsCollector
import com.ailife.android.identity.resolveCollectorVersion
import com.ailife.android.network.testServerReachability
import com.ailife.android.payment.PaymentNotificationFailures
import com.ailife.android.service.ContractEventQueueDrainer
import com.ailife.android.service.HeartbeatQueueDrainer
import com.ailife.android.service.LifeSyncWorker
import com.ailife.android.service.WechatPayNotificationService
import com.ailife.android.update.UpdateCheckState
import com.ailife.android.update.UpdateCheckStateStore
import com.ailife.android.update.UpdateCheckWorker
import com.ailife.android.update.UpdateCodes
import com.ailife.android.system.deviceSupportsNotificationPermission
import com.ailife.android.system.hasUsageAccess
import com.ailife.android.system.isForegroundAccessibilityEnabled
import com.ailife.android.system.isIgnoringBatteryOptimizations
import com.ailife.android.system.isNotificationListenerEnabled
import com.ailife.android.system.openBatterySettings
import com.ailife.android.system.openHealthConnectStore
import com.ailife.android.system.openXiaomiAutostart
import com.ailife.android.system.oemTip
import com.ailife.android.ui.components.DataTypeRow
import com.ailife.android.ui.components.InfoRow
import com.ailife.android.ui.components.ServiceStatusRow
import com.ailife.android.ui.theme.Border
import com.ailife.android.ui.theme.Accent
import com.ailife.android.ui.theme.Primary
import com.ailife.android.ui.theme.Secondary
import com.ailife.android.ui.theme.TextMuted
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZonedDateTime
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AiLifeTopBar(settings: SettingsStore) {
    var connected by remember { mutableStateOf(false) }
    var urlSnapshot by remember { mutableStateOf(settings.serverUrl) }

    LaunchedEffect(Unit) {
        while (true) {
            urlSnapshot = settings.serverUrl
            connected = testServerReachability(urlSnapshot)
            delay(5000L)
        }
    }

    androidx.compose.material3.TopAppBar(
        title = { Text("AI Life") },
        actions = {
            Text(
                text = if (connected) "已连接" else "未连接",
                color = if (connected) Secondary else TextMuted,
                fontSize = 12.sp,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.padding(end = 16.dp),
            )
        },
    )
}

@Composable
fun SetupScreen(settings: SettingsStore) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val scrollState = rememberScrollState()

    var serverInput by remember { mutableStateOf(settings.serverUrl) }
    var tokenInput by remember { mutableStateOf(settings.deviceToken) }
    var deviceIdInput by remember { mutableStateOf(settings.deviceId) }
    var deviceNameInput by remember { mutableStateOf(settings.deviceName) }
    var ownerIdInput by remember { mutableStateOf(settings.ownerId) }
    var showToken by remember { mutableStateOf(false) }
    var statusMsg by remember { mutableStateOf<String?>(null) }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text("服务器配置", style = MaterialTheme.typography.headlineMedium)

        OutlinedTextField(
            value = serverInput,
            onValueChange = { serverInput = it },
            label = { Text("服务器地址") },
            placeholder = { Text("http://10.0.2.2:8787") },
            supportingText = { Text("本地模拟器可用 10.0.2.2，真机请填写局域网或 HTTPS 地址") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
        )

        OutlinedTextField(
            value = tokenInput,
            onValueChange = { tokenInput = it },
            label = { Text("Device Token") },
            singleLine = true,
            visualTransformation = if (showToken) VisualTransformation.None else PasswordVisualTransformation(),
            trailingIcon = {
                TextButton(onClick = { showToken = !showToken }) {
                    Text(if (showToken) "隐藏" else "显示")
                }
            },
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
        )

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = deviceIdInput,
                onValueChange = { deviceIdInput = it },
                label = { Text("Device ID") },
                singleLine = true,
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(8.dp),
            )
            OutlinedTextField(
                value = deviceNameInput,
                onValueChange = { deviceNameInput = it },
                label = { Text("设备名称") },
                singleLine = true,
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(8.dp),
            )
        }

        OutlinedTextField(
            value = ownerIdInput,
            onValueChange = { ownerIdInput = it },
            label = { Text("Owner ID") },
            supportingText = { Text("与服务端 Owner 标识一致（默认 local），否则事件会被拒绝") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
        )

        Button(
            onClick = {
                settings.serverUrl = serverInput
                settings.deviceToken = tokenInput
                settings.deviceId = deviceIdInput
                settings.deviceName = deviceNameInput
                settings.ownerId = ownerIdInput
                LifeSyncWorker.schedule(context)
                statusMsg = "设置已保存，同步任务已更新"
            },
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
            colors = ButtonDefaults.buttonColors(containerColor = Primary),
        ) {
            Text("保存设置")
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedButton(
                onClick = {
                    settings.serverUrl = serverInput
                    settings.deviceToken = tokenInput
                    settings.deviceId = deviceIdInput
                    settings.deviceName = deviceNameInput
                    settings.ownerId = ownerIdInput
                    LifeSyncWorker.syncNow(context)
                    statusMsg = "已触发立即同步"
                },
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(8.dp),
            ) {
                Text("立即同步")
            }
            Button(
                onClick = {
                    scope.launch {
                        settings.serverUrl = serverInput
                        settings.deviceToken = tokenInput
                        settings.deviceId = deviceIdInput
                        settings.deviceName = deviceNameInput
                        settings.ownerId = ownerIdInput
                        val ok = testServerReachability(settings.serverUrl)
                        statusMsg = if (ok) "服务端可达" else "服务端不可达"
                    }
                },
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(8.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Primary),
            ) {
                Text("测试连接")
            }
        }

        statusMsg?.let { msg ->
            Surface(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(8.dp),
                color = MaterialTheme.colorScheme.surfaceVariant,
            ) {
                Text(msg, modifier = Modifier.padding(12.dp), style = MaterialTheme.typography.bodyMedium)
            }
        }

        Surface(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
            color = MaterialTheme.colorScheme.surfaceVariant,
        ) {
            Text(
                text = "Android 端会同步 Health Connect、UsageStats、实时前台应用和微信支付通知。敏感内容在客户端和服务端都按最小化原则处理。",
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted,
                modifier = Modifier.padding(12.dp),
            )
        }
    }
}

@Composable
fun SyncScreen(settings: SettingsStore) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val lifecycleOwner = LocalLifecycleOwner.current
    val healthCollector = remember(context) { HealthConnectCollector(context) }
    val permissionLauncher = rememberLauncherForActivityResult(
        contract = PermissionController.createRequestPermissionResultContract(),
        onResult = {},
    )

    var healthAvailable by remember { mutableStateOf(HealthConnectCollector.isAvailable(context)) }
    var grantedCount by remember { mutableIntStateOf(0) }
    var queuedUsageEvents by remember { mutableIntStateOf(0) }
    var usageFailures by remember { mutableIntStateOf(0) }
    var queuedHealthEvents by remember { mutableIntStateOf(0) }
    var healthFailures by remember { mutableIntStateOf(0) }
    var queuedPaymentEvents by remember { mutableIntStateOf(0) }
    var paymentFailures by remember { mutableIntStateOf(0) }
    var paymentNotificationFailures by remember { mutableIntStateOf(0) }
    var queuedHeartbeats by remember { mutableIntStateOf(0) }
    var statusMsg by remember { mutableStateOf<String?>(null) }

    suspend fun refreshHealthState() {
        healthAvailable = HealthConnectCollector.isAvailable(context)
        grantedCount = if (healthAvailable) {
            runCatching {
                healthCollector.grantedPermissions().intersect(healthCollector.readPermissions).size
            }.getOrDefault(0)
        } else {
            0
        }
        val usageDrainer = ContractEventQueueDrainer(
            context,
            settings,
            LifeSyncWorker.USAGE_QUEUE,
            LifeSyncWorker.USAGE_FAILURES,
        )
        queuedUsageEvents = usageDrainer.queuedCount()
        usageFailures = usageDrainer.failureCount()
        val healthDrainer = ContractEventQueueDrainer(
            context,
            settings,
            LifeSyncWorker.HEALTH_QUEUE,
            LifeSyncWorker.HEALTH_FAILURES,
        )
        queuedHealthEvents = healthDrainer.queuedCount()
        healthFailures = healthDrainer.failureCount()
        val paymentDrainer = ContractEventQueueDrainer(
            context,
            settings,
            LifeSyncWorker.PAYMENT_QUEUE,
            LifeSyncWorker.PAYMENT_FAILURES,
        )
        queuedPaymentEvents = paymentDrainer.queuedCount()
        paymentFailures = paymentDrainer.failureCount()
        paymentNotificationFailures = PaymentNotificationFailures(
            File(context.filesDir, WechatPayNotificationService.PAYMENT_NOTIFICATION_FAILURES),
        ).size()
        queuedHeartbeats = HeartbeatQueueDrainer(context, settings).queuedCount()
    }

    LaunchedEffect(lifecycleOwner) {
        lifecycleOwner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            refreshHealthState()
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("健康数据", style = MaterialTheme.typography.headlineMedium)

        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, Border, RoundedCornerShape(8.dp)),
            shape = RoundedCornerShape(8.dp),
        ) {
            Row(
                modifier = Modifier.padding(12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Health Connect", style = MaterialTheme.typography.titleMedium)
                    Text(
                        text = if (healthAvailable) "已就绪，授权 $grantedCount/${healthCollector.readPermissions.size}" else "不可用或未安装",
                        style = MaterialTheme.typography.bodySmall,
                        color = if (healthAvailable) Secondary else MaterialTheme.colorScheme.error,
                    )
                }
                OutlinedButton(
                    onClick = {
                        if (HealthConnectCollector.isAvailable(context)) {
                            permissionLauncher.launch(healthCollector.readPermissions)
                        } else {
                            openHealthConnectStore(context)
                        }
                    },
                    shape = RoundedCornerShape(8.dp),
                ) {
                    Text(if (healthAvailable) "授权" else "安装")
                }
            }
        }

        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, Border, RoundedCornerShape(8.dp)),
            shape = RoundedCornerShape(8.dp),
        ) {
            Column(
                modifier = Modifier.padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text("同步队列", style = MaterialTheme.typography.titleMedium)
                InfoRow("待上传使用事件（版本化）", queuedUsageEvents.toString())
                InfoRow("使用事件永久失败", usageFailures.toString())
                InfoRow("待上传健康事件（版本化）", queuedHealthEvents.toString())
                InfoRow("健康事件永久失败", healthFailures.toString())
                InfoRow("待上传支付事件（版本化）", queuedPaymentEvents.toString())
                InfoRow("支付事件永久失败", paymentFailures.toString())
                InfoRow("支付通知解析失败（本地留存，不上传）", paymentNotificationFailures.toString())
                InfoRow("待上传心跳", queuedHeartbeats.toString())
                InfoRow("上次健康同步", formatInstantMillis(settings.lastHealthSyncMillis))
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Button(
                onClick = {
                    LifeSyncWorker.syncNow(context)
                    scope.launch {
                        kotlinx.coroutines.delay(300L)
                        refreshHealthState()
                    }
                    statusMsg = "已触发健康和使用数据同步"
                },
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(8.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Primary),
            ) {
                Text("立即同步")
            }
            OutlinedButton(
                onClick = {
                    scope.launch {
                        refreshHealthState()
                        statusMsg = "状态已刷新"
                    }
                },
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(8.dp),
            ) {
                Text("刷新")
            }
        }

        Divider(color = Border, thickness = 1.dp)

        Text("同步数据类型", style = MaterialTheme.typography.titleMedium)
        DataTypeRow("步数", "Health Connect READ_STEPS", healthAvailable && grantedCount > 0)
        DataTypeRow("心率", "Health Connect READ_HEART_RATE", healthAvailable && grantedCount > 0)
        DataTypeRow("睡眠", "Health Connect READ_SLEEP", healthAvailable && grantedCount > 0)
        DataTypeRow("应用使用时长（每日权威来源）", "UsageStats 版本化区间事件", hasUsageAccess(context))
        DataTypeRow("实时前台应用", "Accessibility TYPE_WINDOW_STATE_CHANGED", isForegroundAccessibilityEnabled(context))
        DataTypeRow("微信支付通知", "NotificationListenerService", isNotificationListenerEnabled(context))

        statusMsg?.let { msg ->
            Surface(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(8.dp),
                color = MaterialTheme.colorScheme.surfaceVariant,
            ) {
                Text(msg, modifier = Modifier.padding(12.dp), style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

@Composable
fun PreviewScreen(settings: SettingsStore) {
    val context = LocalContext.current
    val scrollState = rememberScrollState()
    var selectedDayIndex by rememberSaveable { mutableIntStateOf(0) }
    var selectedUsageAppIndex by rememberSaveable { mutableIntStateOf(0) }
    var loading by remember { mutableStateOf(true) }
    var healthAvailable by remember { mutableStateOf(false) }
    var grantedPermissions by remember { mutableStateOf<Set<String>>(emptySet()) }
    var healthSamplesByDay by remember { mutableStateOf<List<Pair<String, List<HealthSample>>>>(emptyList()) }
    var usageEvents by remember { mutableStateOf<List<LifeEvent>>(emptyList()) }

    LaunchedEffect(settings.deviceId) {
        loading = true
        val snapshot = withContext(Dispatchers.IO) {
            val zone = ZoneId.systemDefault()
            val today = LocalDate.now(zone)
            val healthCollector = HealthConnectCollector(context.applicationContext)
            val usageCollector = UsageStatsCollector(context.applicationContext)
            val healthOk = HealthConnectCollector.isAvailable(context)
            val granted = if (healthOk) {
                runCatching { healthCollector.grantedPermissions() }.getOrDefault(emptySet())
            } else {
                emptySet()
            }
            val days = (0 until 7).map { today.minusDays(it.toLong()) }
            val healthRows = days.map { day ->
                val start = day.atStartOfDay(zone).toInstant()
                val end = day.plusDays(1).atStartOfDay(zone).toInstant()
                day.toString() to runCatching { healthCollector.readSamples(start, end) }.getOrDefault(emptyList())
            }
            PreviewSnapshot(
                healthAvailable = healthOk,
                grantedPermissions = granted,
                healthSamplesByDay = healthRows,
                usageEvents = usageCollector.collectRecentDays(settings, 7),
            )
        }
        healthAvailable = snapshot.healthAvailable
        grantedPermissions = snapshot.grantedPermissions
        healthSamplesByDay = snapshot.healthSamplesByDay
        usageEvents = snapshot.usageEvents
        loading = false
    }

    val selectedDay = healthSamplesByDay.getOrNull(selectedDayIndex)?.first
    val selectedHealthSamples = healthSamplesByDay.getOrNull(selectedDayIndex)?.second.orEmpty()
    val selectedUsageEvents = usageEvents.filter { event ->
        selectedDay != null && event.data["date"]?.toString() == selectedDay
    }
    val hourlySteps = remember(selectedDay, selectedHealthSamples) {
        selectedDay?.let { stepsByHour(selectedHealthSamples, LocalDate.parse(it), ZoneId.systemDefault()) } ?: DoubleArray(24)
    }
    val topUsageApps = remember(selectedUsageEvents) { topUsageAppsByHour(selectedUsageEvents) }
    LaunchedEffect(selectedDay, topUsageApps.size) {
        if (selectedUsageAppIndex >= topUsageApps.size) {
            selectedUsageAppIndex = 0
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("数据预览", style = MaterialTheme.typography.headlineMedium)

        Surface(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
            color = MaterialTheme.colorScheme.surfaceVariant,
        ) {
            Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("读取能力", style = MaterialTheme.typography.titleMedium)
                InfoRow("Health Connect", if (healthAvailable) "可用" else "不可用")
                InfoRow("步数", permissionText(HealthConnectCollector.stepsReadPermission(), grantedPermissions))
                InfoRow("心率", permissionText(HealthConnectCollector.heartRateReadPermission(), grantedPermissions))
                InfoRow("睡眠", permissionText(HealthConnectCollector.sleepReadPermission(), grantedPermissions))
                InfoRow("UsageStats", if (UsageStatsCollector.hasUsageAccess(context)) "已授权" else "未授权")
            }
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            healthSamplesByDay.forEachIndexed { index, (day, _) ->
                val selected = selectedDayIndex == index
                if (selected) {
                    Button(
                        onClick = { selectedDayIndex = index },
                        shape = RoundedCornerShape(8.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = Primary),
                    ) {
                        Text(dayLabel(day))
                    }
                } else {
                    OutlinedButton(
                        onClick = { selectedDayIndex = index },
                        shape = RoundedCornerShape(8.dp),
                    ) {
                        Text(dayLabel(day))
                    }
                }
            }
        }

        if (loading) {
            Surface(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(8.dp),
                color = MaterialTheme.colorScheme.surfaceVariant,
            ) {
                Text("正在读取本机可访问数据...", modifier = Modifier.padding(12.dp), style = MaterialTheme.typography.bodyMedium)
            }
        }

        Text("${selectedDay ?: "所选日期"} 健康数据", style = MaterialTheme.typography.titleMedium)
        DayHealthSummaryCard(selectedHealthSamples)
        HourlyStepsCard(hourlySteps)
        HealthPreviewCard(selectedHealthSamples)

        Text("${selectedDay ?: "所选日期"} 前台应用时长", style = MaterialTheme.typography.titleMedium)
        UsageSummaryCard(selectedUsageEvents)
        HourlyUsageCard(
            apps = topUsageApps,
            selectedIndex = selectedUsageAppIndex,
            onPrevious = {
                if (topUsageApps.isNotEmpty()) {
                    selectedUsageAppIndex = (selectedUsageAppIndex + topUsageApps.size - 1) % topUsageApps.size
                }
            },
            onNext = {
                if (topUsageApps.isNotEmpty()) {
                    selectedUsageAppIndex = (selectedUsageAppIndex + 1) % topUsageApps.size
                }
            },
        )
        PreviewCard("UsageStats 明细", selectedUsageEvents)
    }
}

@Composable
fun StatusScreen(settings: SettingsStore) {
    val context = LocalContext.current
    val scrollState = rememberScrollState()
    val lifecycleOwner = LocalLifecycleOwner.current
    val pm = remember { context.getSystemService(Context.POWER_SERVICE) as? PowerManager }

    var tick by remember { mutableIntStateOf(0) }
    var batteryIgnored by remember {
        mutableStateOf(pm?.isIgnoringBatteryOptimizations(context.packageName) == true)
    }

    LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(3000L)
            tick++
        }
    }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                batteryIgnored = isIgnoringBatteryOptimizations(context)
                tick++
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    val usageGranted = remember(tick) { hasUsageAccess(context) }
    val foregroundEnabled = remember(tick) { isForegroundAccessibilityEnabled(context) }
    val notificationEnabled = remember(tick) { isNotificationListenerEnabled(context) }
    val manufacturer = remember { Build.MANUFACTURER.lowercase(Locale.ROOT) }
    val usageDrainer = remember(tick) {
        ContractEventQueueDrainer(context, settings, LifeSyncWorker.USAGE_QUEUE, LifeSyncWorker.USAGE_FAILURES)
    }
    val queuedUsageEvents = usageDrainer.queuedCount()
    val usageFailures = usageDrainer.failureCount()
    val healthDrainer = remember(tick) {
        ContractEventQueueDrainer(context, settings, LifeSyncWorker.HEALTH_QUEUE, LifeSyncWorker.HEALTH_FAILURES)
    }
    val queuedHealthEvents = healthDrainer.queuedCount()
    val healthFailures = healthDrainer.failureCount()
    val paymentDrainer = remember(tick) {
        ContractEventQueueDrainer(context, settings, LifeSyncWorker.PAYMENT_QUEUE, LifeSyncWorker.PAYMENT_FAILURES)
    }
    val queuedPaymentEvents = paymentDrainer.queuedCount()
    val paymentFailures = paymentDrainer.failureCount()
    val paymentNotificationFailures = remember(tick) {
        PaymentNotificationFailures(
            File(context.filesDir, WechatPayNotificationService.PAYMENT_NOTIFICATION_FAILURES),
        ).size()
    }
    val queuedHeartbeats = remember(tick) { HeartbeatQueueDrainer(context, settings).queuedCount() }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("权限状态", style = MaterialTheme.typography.titleMedium)

        ServiceStatusRow("Usage Access", usageGranted) {
            context.startActivity(Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS))
        }
        ServiceStatusRow("实时前台应用", foregroundEnabled) {
            context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        ServiceStatusRow("微信支付通知", notificationEnabled) {
            context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        ServiceStatusRow("电池优化已忽略", batteryIgnored) {
            openBatterySettings(context)
        }
        if (deviceSupportsNotificationPermission()) {
            val notificationPermission = remember(tick) {
                context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
            }
            ServiceStatusRow("通知权限", notificationPermission) {
                context.startActivity(
                    Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                        putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                    },
                )
            }
        }

        if (manufacturer.contains("xiaomi") || manufacturer.contains("redmi")) {
            ServiceStatusRow("自启动权限", false) {
                openXiaomiAutostart(context)
            }
        }

        val oemTip = oemTip(manufacturer)
        if (oemTip != null) {
            Surface(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(8.dp),
                color = MaterialTheme.colorScheme.tertiaryContainer,
            ) {
                Column(modifier = Modifier.padding(12.dp)) {
                    Text(
                        text = "厂商特殊设置",
                        style = MaterialTheme.typography.labelLarge,
                        color = MaterialTheme.colorScheme.onTertiaryContainer,
                    )
                    Spacer(modifier = Modifier.height(4.dp))
                    Text(
                        text = oemTip,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onTertiaryContainer,
                    )
                }
            }
        }

        Surface(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(8.dp),
            color = MaterialTheme.colorScheme.surfaceVariant,
        ) {
            Text(
                text = "如遇后台同步异常，请检查电池优化、自启动、Usage Access、无障碍和通知监听权限。",
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted,
                modifier = Modifier.padding(12.dp),
            )
        }

        Divider(color = Border, thickness = 1.dp)

        Text("运行状态", style = MaterialTheme.typography.titleMedium)
        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 120.dp)
                .border(1.dp, Border, RoundedCornerShape(8.dp)),
            shape = RoundedCornerShape(8.dp),
        ) {
            Column(
                modifier = Modifier.padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                InfoRow("Server", settings.serverUrl.ifBlank { "未配置" })
                InfoRow("Device ID", settings.deviceId)
                InfoRow("Device Name", settings.deviceName)
                InfoRow("Owner ID", settings.ownerId)
                InfoRow("Queued Usage Events", queuedUsageEvents.toString())
                InfoRow("Usage Sync Failures", usageFailures.toString())
                InfoRow("Queued Health Events", queuedHealthEvents.toString())
                InfoRow("Health Sync Failures", healthFailures.toString())
                InfoRow("Queued Payment Events", queuedPaymentEvents.toString())
                InfoRow("Payment Sync Failures", paymentFailures.toString())
                InfoRow("Payment Notification Failures (local)", paymentNotificationFailures.toString())
                InfoRow("Queued Heartbeats", queuedHeartbeats.toString())
                InfoRow("Last Usage Day", settings.lastUsageSyncDay.ifBlank { "无" })
            }
        }

        Divider(color = Border, thickness = 1.dp)

        Text("应用更新", style = MaterialTheme.typography.titleMedium)
        UpdateStatusCard(settings, tick)
    }
}

/**
 * Notify-only component update status (ticket 17): the app checks the
 * android component's own update manifest, reports the diagnosable outcome,
 * and links to the download page. It never downloads or installs an APK by
 * itself, so no unknown-sources flow exists in V1.
 */
@Composable
private fun UpdateStatusCard(settings: SettingsStore, tick: Int) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val updateState = remember(tick) {
        UpdateCheckStateStore(File(context.filesDir, UpdateCheckWorker.UPDATE_STATE_DIRECTORY)).read()
    }
    val currentVersion = remember { resolveCollectorVersion(context) }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            InfoRow("当前版本", "v$currentVersion")
            InfoRow(
                "更新状态",
                when (updateState.state) {
                    UpdateCheckState.IDLE -> "尚未检查"
                    UpdateCheckState.UP_TO_DATE -> "已是最新"
                    UpdateCheckState.AVAILABLE -> "发现新版本 v${updateState.availableVersion}（请从下载页安装）"
                    UpdateCheckState.INCOMPATIBLE ->
                        "发现新版本 v${updateState.availableVersion}，但需先手动升级到最低兼容版本"
                    UpdateCheckState.FAILED -> "检查失败（${updateState.errorCode ?: UpdateCodes.MANIFEST_FETCH_FAILED}）"
                },
            )
            InfoRow("上次检查", formatInstantMillis(updateState.lastCheckAtMillis ?: 0L))
            if (!updateState.errorMessage.isNullOrBlank()) {
                Text(
                    text = updateState.errorMessage,
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted,
                )
            }
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Checkbox(
                    checked = settings.updateCheckEnabled,
                    onCheckedChange = { settings.updateCheckEnabled = it },
                )
                Text("启用更新检查", style = MaterialTheme.typography.bodyMedium)
                Spacer(modifier = Modifier.weight(1f))
                TextButton(
                    onClick = {
                        settings.updateCheckEnabled = true
                        UpdateCheckWorker.checkNow(context)
                    },
                ) {
                    Text("检查更新")
                }
                if (updateState.state == UpdateCheckState.AVAILABLE && !updateState.downloadUrl.isNullOrBlank()) {
                    TextButton(onClick = {
                        context.startActivity(
                            Intent(Intent.ACTION_VIEW, android.net.Uri.parse(updateState.downloadUrl)),
                        )
                    }) {
                        Text("打开下载页")
                    }
                }
            }
        }
    }
}

private fun formatInstantMillis(value: Long): String {
    if (value <= 0L) return "无"
    return java.time.Instant.ofEpochMilli(value).toString()
}

private data class PreviewSnapshot(
    val healthAvailable: Boolean,
    val grantedPermissions: Set<String>,
    val healthSamplesByDay: List<Pair<String, List<HealthSample>>>,
    val usageEvents: List<LifeEvent>,
)

private data class UsageSlice(
    val label: String,
    val minutes: Double,
    val color: Color,
)

private data class UsageAppHourly(
    val packageName: String,
    val label: String,
    val minutes: Double,
    val hourlyMinutes: DoubleArray,
)

private fun permissionText(permission: String, grantedPermissions: Set<String>): String {
    return if (permission in grantedPermissions) "已授权" else "未授权"
}

@Composable
private fun DayHealthSummaryCard(samples: List<HealthSample>) {
    val steps = samples.filterIsInstance<HealthStepsSample>().sumOf { it.count }
    val heartRates = samples.filterIsInstance<HealthHeartRateSample>().map { it.beatsPerMinute }
    val sleepMinutes = samples.filterIsInstance<HealthSleepSample>().sumOf { sample ->
        val end = sample.endMillis ?: return@sumOf 0.0
        java.time.Duration.ofMillis((end - sample.startMillis).coerceAtLeast(0)).toMinutes().toDouble()
    }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text("当天汇总", style = MaterialTheme.typography.titleMedium)
            InfoRow("步数", roundPreviewValue(steps.toDouble()))
            InfoRow("平均心率", if (heartRates.isEmpty()) "暂无" else "${roundPreviewValue(heartRates.average())}bpm")
            InfoRow("睡眠区间（来源提供）", if (sleepMinutes <= 0.0) "暂无" else "${roundPreviewValue(sleepMinutes)}min")
            InfoRow("健康记录", samples.size.toString())
        }
    }
}

@Composable
private fun HourlyStepsCard(hourlySteps: DoubleArray) {
    val maxSteps = hourlySteps.maxOrNull()?.coerceAtLeast(1.0) ?: 1.0
    val totalSteps = hourlySteps.sum()
    var selectedHour by rememberSaveable { mutableStateOf<Int?>(null) }
    val selectedSteps = selectedHour?.let { hourlySteps.getOrNull(it) }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("每小时步数", style = MaterialTheme.typography.titleMedium)
            InfoRow("全天步数", roundPreviewValue(totalSteps))
            InfoRow("选中小时", selectedHour?.let { hourRangeLabel(it) } ?: "未选择")
            InfoRow("小时步数", selectedSteps?.let { roundPreviewValue(it) } ?: "未选择")
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(136.dp),
                horizontalArrangement = Arrangement.spacedBy(2.dp),
                verticalAlignment = Alignment.Bottom,
            ) {
                hourlySteps.forEachIndexed { hour, steps ->
                    val selected = selectedHour == hour
                    val barHeight = ((steps / maxSteps) * 120.0).coerceIn(2.0, 120.0)
                    Surface(
                        modifier = Modifier
                            .weight(1f)
                            .height(barHeight.dp)
                            .clickable { selectedHour = hour },
                        shape = RoundedCornerShape(topStart = 3.dp, topEnd = 3.dp),
                        color = when {
                            selected -> Accent
                            steps > 0.0 -> Primary
                            else -> Border
                        },
                    ) {}
                }
            }
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("00", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                Text("06", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                Text("12", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                Text("18", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                Text("23", style = MaterialTheme.typography.bodySmall, color = TextMuted)
            }
        }
    }
}

@Composable
private fun UsageSummaryCard(events: List<LifeEvent>) {
    val totalMinutes = events.sumOf { it.value ?: 0.0 }
    val slices = usageDistributionSlices(events)

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("当天前台时长", style = MaterialTheme.typography.titleMedium)
            InfoRow("总时长", "${roundPreviewValue(totalMinutes)}min")
            InfoRow("应用数", events.size.toString())
            if (slices.isEmpty()) {
                Text("暂无前台应用时长", style = MaterialTheme.typography.bodySmall, color = TextMuted)
            } else {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    DoughnutChart(
                        slices = slices,
                        modifier = Modifier
                            .weight(0.9f)
                            .aspectRatio(1f),
                    )
                    Column(
                        modifier = Modifier.weight(1.2f),
                        verticalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        slices.forEach { slice ->
                            DoughnutLegendRow(slice, totalMinutes)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun DoughnutChart(slices: List<UsageSlice>, modifier: Modifier = Modifier) {
    val total = slices.sumOf { it.minutes }.coerceAtLeast(1.0)

    Canvas(modifier = modifier) {
        val strokeWidth = size.minDimension * 0.18f
        val diameter = size.minDimension - strokeWidth
        val topLeft = Offset(
            x = (size.width - diameter) / 2f,
            y = (size.height - diameter) / 2f,
        )
        val arcSize = Size(diameter, diameter)
        var startAngle = -90f

        slices.forEach { slice ->
            val sweep = (slice.minutes / total * 360.0).toFloat()
            drawArc(
                color = slice.color,
                startAngle = startAngle,
                sweepAngle = sweep,
                useCenter = false,
                topLeft = topLeft,
                size = arcSize,
                style = Stroke(width = strokeWidth, cap = StrokeCap.Butt),
            )
            startAngle += sweep
        }
    }
}

@Composable
private fun DoughnutLegendRow(slice: UsageSlice, totalMinutes: Double) {
    val percent = if (totalMinutes <= 0.0) 0.0 else slice.minutes / totalMinutes * 100.0
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(10.dp)
                .background(slice.color, RoundedCornerShape(2.dp)),
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(slice.label, style = MaterialTheme.typography.bodySmall)
            Text(
                "${roundPreviewValue(slice.minutes)}min · ${roundPreviewValue(percent)}%",
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted,
            )
        }
    }
}

@Composable
private fun HourlyUsageCard(
    apps: List<UsageAppHourly>,
    selectedIndex: Int,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
) {
    val selectedApp = apps.getOrNull(selectedIndex)
    val hourlyMinutes = selectedApp?.hourlyMinutes ?: DoubleArray(24)
    val maxMinutes = hourlyMinutes.maxOrNull()?.coerceAtLeast(1.0) ?: 1.0
    val totalMinutes = hourlyMinutes.sum()
    val canSwitch = apps.size > 1
    var selectedHour by rememberSaveable { mutableStateOf<Int?>(null) }
    val selectedMinutes = selectedHour?.let { hourlyMinutes.getOrNull(it) }

    LaunchedEffect(selectedApp?.packageName) {
        selectedHour = null
    }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    "应用每小时使用",
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium,
                )
                OutlinedButton(
                    onClick = onPrevious,
                    enabled = canSwitch,
                    shape = RoundedCornerShape(8.dp),
                ) {
                    Text("<")
                }
                OutlinedButton(
                    onClick = onNext,
                    enabled = canSwitch,
                    shape = RoundedCornerShape(8.dp),
                ) {
                    Text(">")
                }
            }

            if (selectedApp == null) {
                Text("暂无可预览应用", style = MaterialTheme.typography.bodySmall, color = TextMuted)
            } else {
                InfoRow("当前应用", "${selectedIndex + 1}/${apps.size} ${selectedApp.label}")
                InfoRow("当天时长", "${roundPreviewValue(totalMinutes)}min")
                InfoRow("选中小时", selectedHour?.let { hourRangeLabel(it) } ?: "未选择")
                InfoRow("小时使用", selectedMinutes?.let { "${roundPreviewValue(it)}min" } ?: "未选择")
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(136.dp),
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                    verticalAlignment = Alignment.Bottom,
                ) {
                    hourlyMinutes.forEachIndexed { hour, minutes ->
                        val selected = selectedHour == hour
                        val barHeight = ((minutes / maxMinutes) * 120.0).coerceIn(2.0, 120.0)
                        Surface(
                            modifier = Modifier
                                .weight(1f)
                                .height(barHeight.dp)
                                .clickable { selectedHour = hour },
                            shape = RoundedCornerShape(topStart = 3.dp, topEnd = 3.dp),
                            color = when {
                                selected -> Accent
                                minutes > 0.0 -> Secondary
                                else -> Border
                            },
                        ) {}
                    }
                }
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text("00", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                    Text("06", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                    Text("12", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                    Text("18", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                    Text("23", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                }
            }
        }
    }
}

/** Local preview of Health Connect samples with their data origin attribution. */
@Composable
private fun HealthPreviewCard(samples: List<HealthSample>) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text("健康明细（含数据来源）", style = MaterialTheme.typography.titleMedium)
            if (samples.isEmpty()) {
                Text("暂无数据", style = MaterialTheme.typography.bodySmall, color = TextMuted)
            } else {
                InfoRow("记录数", samples.size.toString())
                samples.take(10).forEach { sample ->
                    InfoRow(healthSampleLabel(sample), healthSampleValue(sample))
                }
                if (samples.size > 10) {
                    Text("还有 ${samples.size - 10} 条未显示", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                }
            }
        }
    }
}

private fun healthSampleLabel(sample: HealthSample): String = when (sample.kind) {
    HealthSampleKind.STEPS -> "步数 · ${sample.dataOrigin}"
    HealthSampleKind.HEART_RATE -> "心率 · ${sample.dataOrigin}"
    HealthSampleKind.SLEEP -> "睡眠 · ${sample.dataOrigin}"
}

private fun healthSampleValue(sample: HealthSample): String = when (sample) {
    is HealthStepsSample -> "${sample.count} 步"
    is HealthHeartRateSample -> "${sample.beatsPerMinute} bpm"
    is HealthSleepSample -> {
        val end = sample.endMillis
        if (end == null) "区间缺失" else java.time.Duration.ofMillis((end - sample.startMillis).coerceAtLeast(0)).toMinutes().toString() + "min"
    }
}

@Composable
private fun PreviewCard(title: String, events: List<LifeEvent>) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium)
            if (events.isEmpty()) {
                Text("暂无数据", style = MaterialTheme.typography.bodySmall, color = TextMuted)
            } else {
                InfoRow("记录数", events.size.toString())
                events.take(10).forEach { event ->
                    InfoRow(eventLabel(event), eventValue(event))
                }
                if (events.size > 10) {
                    Text("还有 ${events.size - 10} 条未显示", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                }
            }
        }
    }
}

private fun eventLabel(event: LifeEvent): String {
    val app = event.data["app_name"]?.toString()?.takeIf { it.isNotBlank() }
        ?: event.data["package_name"]?.toString()
    return app ?: event.type
}

private fun eventValue(event: LifeEvent): String {
    val value = event.value
    val unit = event.unit.orEmpty()
    return when {
        value != null -> "${roundPreviewValue(value)}$unit"
        event.endAt != null -> "${event.startAt} - ${event.endAt}"
        else -> event.startAt
    }
}

private fun usageDistributionSlices(events: List<LifeEvent>): List<UsageSlice> {
    val colors = listOf(
        Primary,
        Secondary,
        Accent,
        Color(0xFFB39DDB),
        Color(0xFF90CAF9),
        TextMuted,
    )
    val ranked = events
        .groupBy { it.data["package_name"]?.toString() ?: "unknown" }
        .mapValues { (_, rows) -> rows.sumOf { it.value ?: 0.0 } }
        .filterValues { it > 0.0 }
        .entries
        .sortedByDescending { it.value }

    val top = ranked.take(5)
    val otherMinutes = ranked.drop(5).sumOf { it.value }
    val slices = top.mapIndexed { index, entry ->
        val label = events
            .firstOrNull { it.data["package_name"]?.toString() == entry.key }
            ?.data
            ?.get("app_name")
            ?.toString()
            ?.takeIf { it.isNotBlank() }
            ?: entry.key
        UsageSlice(
            label = shortAppLabel(label),
            minutes = entry.value,
            color = colors[index],
        )
    }.toMutableList()

    if (otherMinutes > 0.0) {
        slices += UsageSlice("其他", otherMinutes, colors[5])
    }
    return slices
}

private fun topUsageAppsByHour(events: List<LifeEvent>): List<UsageAppHourly> {
    return events
        .groupBy { it.data["package_name"]?.toString() ?: "unknown" }
        .map { (packageName, rows) ->
            val hourly = DoubleArray(24)
            rows.forEach { row ->
                val rowHourly = hourlyMinutes(row)
                rowHourly.forEachIndexed { index, minutes ->
                    hourly[index] += minutes
                }
            }
            val appName = rows
                .firstNotNullOfOrNull { it.data["app_name"]?.toString()?.takeIf { name -> name.isNotBlank() } }
                ?: packageName
            UsageAppHourly(
                packageName = packageName,
                label = shortAppLabel(appName),
                minutes = rows.sumOf { it.value ?: 0.0 },
                hourlyMinutes = hourly,
            )
        }
        .filter { it.minutes > 0.0 }
        .sortedByDescending { it.minutes }
        .take(5)
}

private fun hourlyMinutes(event: LifeEvent): DoubleArray {
    val values = event.data["hourly_minutes"] as? List<*> ?: return DoubleArray(24)
    val hourly = DoubleArray(24)
    values.take(24).forEachIndexed { index, value ->
        hourly[index] = (value as? Number)?.toDouble() ?: 0.0
    }
    return hourly
}

private fun shortAppLabel(packageName: String): String {
    if (packageName.length <= 22) return packageName
    return packageName.substringAfterLast('.').takeIf { it.isNotBlank() }?.take(22) ?: packageName.take(22)
}

private fun roundPreviewValue(value: Double): String {
    return if (value % 1.0 == 0.0) value.toLong().toString() else String.format(Locale.US, "%.2f", value)
}

private fun hourRangeLabel(hour: Int): String {
    val start = hour.coerceIn(0, 23)
    return String.format(Locale.US, "%02d:00-%02d:00", start, start + 1)
}

private fun dayLabel(day: String): String {
    val parsed = runCatching { LocalDate.parse(day) }.getOrNull() ?: return day
    return "${parsed.monthValue}/${parsed.dayOfMonth}"
}

private fun stepsByHour(samples: List<HealthSample>, day: LocalDate, zone: ZoneId): DoubleArray {
    val result = DoubleArray(24)
    val dayStart = day.atStartOfDay(zone)

    samples
        .filterIsInstance<HealthStepsSample>()
        .forEach { sample ->
            val end = sample.endMillis ?: return@forEach
            val start = java.time.Instant.ofEpochMilli(sample.startMillis).atZone(zone)
            val endZoned = java.time.Instant.ofEpochMilli(end).atZone(zone)
            val totalMs = java.time.Duration.between(start, endZoned).toMillis().coerceAtLeast(1L)
            val steps = sample.count.toDouble()

            for (hour in 0 until 24) {
                val hourStart = dayStart.plusHours(hour.toLong())
                val hourEnd = hourStart.plusHours(1)
                val overlapMs = overlapMillis(start, endZoned, hourStart, hourEnd)
                if (overlapMs > 0L) {
                    result[hour] += steps * overlapMs.toDouble() / totalMs.toDouble()
                }
            }
        }

    return result
}

private fun overlapMillis(start: ZonedDateTime, end: ZonedDateTime, rangeStart: ZonedDateTime, rangeEnd: ZonedDateTime): Long {
    val overlapStart = maxOf(start.toInstant().toEpochMilli(), rangeStart.toInstant().toEpochMilli())
    val overlapEnd = minOf(end.toInstant().toEpochMilli(), rangeEnd.toInstant().toEpochMilli())
    return (overlapEnd - overlapStart).coerceAtLeast(0L)
}
