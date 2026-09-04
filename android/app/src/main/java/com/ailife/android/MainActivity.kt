package com.ailife.android

import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.app.ActivityCompat
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import com.ailife.android.data.SettingsStore
import com.ailife.android.service.LifeSyncWorker
import com.ailife.android.ui.screens.AiLifeTopBar
import com.ailife.android.ui.screens.PreviewScreen
import com.ailife.android.ui.screens.SyncScreen
import com.ailife.android.ui.screens.SetupScreen
import com.ailife.android.ui.screens.StatusScreen
import com.ailife.android.ui.theme.AiLifeTheme
import com.ailife.android.update.UpdateCheckWorker

class MainActivity : ComponentActivity() {
    private lateinit var settingsStore: SettingsStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        settingsStore = SettingsStore(this)
        enableEdgeToEdge()
        requestNotificationPermission()

        setContent {
            AiLifeTheme {
                Scaffold(
                    modifier = Modifier.fillMaxSize(),
                    topBar = { AiLifeTopBar(settingsStore) },
                ) { innerPadding ->
                    MainContent(settingsStore, Modifier.fillMaxSize().padding(innerPadding))
                }
            }
        }

        LifeSyncWorker.schedule(this)
        UpdateCheckWorker.schedule(this)
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(android.Manifest.permission.POST_NOTIFICATIONS),
                1001,
            )
        }
    }
}

@Composable
private fun MainContent(settings: SettingsStore, modifier: Modifier = Modifier) {
    var selectedTab by rememberSaveable { mutableIntStateOf(0) }
    val tabs = listOf("设置", "同步", "状态", "预览")

    androidx.compose.foundation.layout.Column(modifier = modifier) {
        TabRow(
            selectedTabIndex = selectedTab,
            containerColor = MaterialTheme.colorScheme.surface,
            contentColor = MaterialTheme.colorScheme.primary,
        ) {
            tabs.forEachIndexed { index, title ->
                Tab(
                    selected = selectedTab == index,
                    onClick = { selectedTab = index },
                    text = { Text(title) },
                )
            }
        }

        when (selectedTab) {
            0 -> SetupScreen(settings)
            1 -> SyncScreen(settings)
            2 -> StatusScreen(settings)
            3 -> PreviewScreen(settings)
        }
    }
}
