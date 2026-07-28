package com.ailife.android.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.ailife.android.data.SettingsStore

class BootCompletedReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        val action = intent?.action ?: return
        if (action != Intent.ACTION_BOOT_COMPLETED && action != Intent.ACTION_MY_PACKAGE_REPLACED) return

        val settings = SettingsStore(context)
        if (!settings.isReady()) return

        LifeSyncWorker.schedule(context)
        LifeSyncWorker.syncNow(context)
    }
}
