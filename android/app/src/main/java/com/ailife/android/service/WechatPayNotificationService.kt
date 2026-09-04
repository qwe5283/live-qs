package com.ailife.android.service

import android.content.Context
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.ailife.android.data.SettingsStore
import com.ailife.android.identity.resolveCollectorVersion
import com.ailife.android.payment.PaymentNotificationFailure
import com.ailife.android.payment.PaymentNotificationFailures
import com.ailife.android.payment.PaymentParseResult
import com.ailife.android.payment.WechatPayNotificationParser
import com.ailife.android.payment.WechatPaySyncState
import com.ailife.android.payment.WechatPayTransactionPlanner
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import java.io.File
import java.time.Instant
import java.time.ZoneId

/**
 * Notification-listener collector for WeChat payment notifications. The
 * listener registration, permission flow, and on-device parsing behavior are
 * unchanged; the output path is the versioned contract protocol:
 *
 * - each notification is parsed on-device into minimal structured facts
 *   (amount, currency, direction, approved merchant label, category);
 * - the raw notification text never leaves the device — it exists only in the
 *   local failure queue when a payment notification cannot be parsed, and
 *   never enters the outbox, upload requests, server events, or logs;
 * - parsed transactions get a stable UUIDv5 event identity (device id +
 *   install GUID + notification key + post time) so duplicate deliveries and
 *   retries are answered as duplicates and never double-book;
 * - notifications that look like an already-reported payment are uploaded
 *   flagged `pending_confirmation` instead of being merged on amount and
 *   time.
 */
class WechatPayNotificationService : NotificationListenerService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn?.packageName != WECHAT_PACKAGE) return
        val notificationKey = sbn.key ?: return
        val postedAtMillis = sbn.postTime

        val settings = SettingsStore(this)
        if (!settings.isReady()) return

        val extras = sbn.notification.extras
        val title = extras.getCharSequence(EXTRA_TITLE)?.toString().orEmpty()
        val text = extras.getCharSequence(EXTRA_TEXT)?.toString().orEmpty()
        val bigText = extras.getCharSequence(EXTRA_BIG_TEXT)?.toString().orEmpty()

        when (val parsed = WechatPayNotificationParser.parse(notificationKey, postedAtMillis, title, text, bigText)) {
            is PaymentParseResult.NotPayment -> return
            is PaymentParseResult.Failure -> scope.launch {
                // Local diagnosis only: the raw text is written to the local
                // failure file and never reaches the upload path.
                PaymentNotificationFailures(File(filesDir, PAYMENT_NOTIFICATION_FAILURES)).record(
                    PaymentNotificationFailure(
                        sourceFingerprint = "$notificationKey|$postedAtMillis",
                        reason = parsed.reason,
                        postedAt = Instant.ofEpochMilli(postedAtMillis).toString(),
                        title = title,
                        text = text,
                        recordedAt = Instant.now().toString(),
                    ),
                )
            }
            is PaymentParseResult.Transaction -> scope.launch { upload(parsed) }
        }
    }

    private suspend fun upload(transaction: PaymentParseResult.Transaction) {
        val settings = SettingsStore(this)
        val state = WechatPaySyncState(stateFile(this))
        val nowMillis = System.currentTimeMillis()
        val plan = WechatPayTransactionPlanner.plan(
            transactions = listOf(transaction),
            state = state.transactions,
            deviceId = settings.deviceId,
            ownerId = settings.ownerId,
            installGuid = state.installGuid,
            nowMillis = nowMillis,
            collectorVersion = resolveCollectorVersion(this),
            zone = ZoneId.systemDefault(),
        )

        for ((eventId, transactionState) in plan.states) {
            state.record(eventId, transactionState)
        }
        state.prunePostedBefore(nowMillis - STATE_RETENTION_MS)
        state.save()

        val drainer = ContractEventQueueDrainer(this, settings, PAYMENT_QUEUE, PAYMENT_FAILURES)
        drainer.enqueue(plan.events)
        drainer.drainOnce()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        private const val WECHAT_PACKAGE = "com.tencent.mm"
        private const val EXTRA_TITLE = "android.title"
        private const val EXTRA_TEXT = "android.text"
        private const val EXTRA_BIG_TEXT = "android.bigText"
        private const val STATE_RETENTION_MS = 7L * 24 * 60 * 60 * 1000
        private const val STATE_FILE_NAME = "payment-sync-state.json"

        /** Local-only failure file for unparseable payment notifications. */
        const val PAYMENT_NOTIFICATION_FAILURES = "payment-notification-failures.ndjson"

        /** Outbox and rejection-queue file names, shared with the sync/status screens. */
        const val PAYMENT_QUEUE = "payment-events.ndjson"
        const val PAYMENT_FAILURES = "payment-sync-failures.ndjson"

        fun stateFile(context: Context) = File(context.filesDir, STATE_FILE_NAME)
    }
}
