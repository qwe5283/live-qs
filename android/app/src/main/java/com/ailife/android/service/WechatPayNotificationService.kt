package com.ailife.android.service

import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.ailife.android.data.SettingsStore
import com.ailife.android.data.model.LifeEvent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import java.security.MessageDigest
import java.time.Instant

class WechatPayNotificationService : NotificationListenerService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val recentIds = ArrayDeque<String>()
    private val recentSet = mutableSetOf<String>()

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn?.packageName != WECHAT_PACKAGE) return

        val parsed = parsePaymentNotification(sbn) ?: return
        val settings = SettingsStore(this)
        if (!settings.isReady()) return

        val idempotencyKey = "wechat-notification-${sha256("${sbn.key}|${sbn.postTime}|${parsed.amount}|${parsed.merchant}")}"
        if (!remember(idempotencyKey)) return

        val event = LifeEvent(
            idempotencyKey = idempotencyKey,
            bucket = "android:${settings.deviceId}:wechat-pay",
            type = "payment.transaction",
            startAt = Instant.ofEpochMilli(sbn.postTime).toString(),
            value = if (parsed.direction == "income") parsed.amount else -parsed.amount,
            unit = "CNY",
            privacyLevel = "sensitive",
            data = mapOf(
                "merchant" to parsed.merchant,
                "direction" to parsed.direction,
                "category" to categorize(parsed.merchant),
                "payment_method" to "wechat",
                "source" to "notification",
            ),
        )

        scope.launch {
            val drainer = EventQueueDrainer(this@WechatPayNotificationService, settings)
            drainer.enqueue(listOf(event))
            drainer.drainOnce()
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun parsePaymentNotification(sbn: StatusBarNotification): ParsedPayment? {
        val extras = sbn.notification.extras
        val title = extras.getCharSequence("android.title")?.toString().orEmpty()
        val text = extras.getCharSequence("android.text")?.toString().orEmpty()
        val bigText = extras.getCharSequence("android.bigText")?.toString().orEmpty()
        val content = listOf(title, text, bigText).filter { it.isNotBlank() }.joinToString(" ")
        if (content.isBlank()) return null
        if (!PAYMENT_KEYWORDS.any { content.contains(it) }) return null

        val amount = AMOUNT_REGEX.find(content)?.groups?.get(1)?.value?.toDoubleOrNull() ?: return null
        if (amount <= 0.0) return null

        val direction = if (INCOME_KEYWORDS.any { content.contains(it) }) "income" else "expense"
        return ParsedPayment(
            amount = amount,
            direction = direction,
            merchant = extractMerchant(title, text, direction),
        )
    }

    private fun extractMerchant(title: String, text: String, direction: String): String {
        val candidate = if (direction == "income") title else text.ifBlank { title }
        return candidate
            .replace(AMOUNT_REGEX, "")
            .replace("微信支付", "")
            .replace("支付成功", "")
            .replace("收款到账", "")
            .replace("已收款", "")
            .trim(' ', '-', '，', ',', ':', '：')
            .take(80)
            .ifBlank { "WeChat Pay" }
    }

    private fun categorize(merchant: String): String {
        return when {
            Regex("美团|饿了么|大众点评|餐|饭|食堂|咖啡|奶茶|茶饮|超市|便利店|盒马|麦当劳|肯德基|必胜客|星巴克|瑞幸|喜茶|奈雪|kfc|starbucks|luckin|coffee", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> "food"
            Regex("滴滴|小拉|打车|地铁|公交|铁路|12306|机票|航旅|高德|停车|加油|充电|高速|etc|taxi|metro", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> "transport"
            Regex("京东|淘宝|天猫|拼多多|抖音商城|小红书|唯品会|得物|苏宁|商城|购物|数码|服饰|家居").containsMatchIn(merchant) -> "shopping"
            Regex("水费|电费|燃气|话费|宽带|物业|房租|租金|保险|税|账单|生活缴费").containsMatchIn(merchant) -> "bills"
            Regex("医院|药|医保|体检|诊所|口腔|牙|挂号|pharmacy|clinic|hospital", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> "health"
            Regex("课程|教育|培训|学校|大学|考试|书店|图书|知识付费|得到|极客时间|coursera|udemy", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> "education"
            Regex("电影|影院|游戏|音乐|会员|视频|优酷|腾讯视频|爱奇艺|哔哩|bilibili|steam|spotify|netflix", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> "entertainment"
            Regex("转账|红包").containsMatchIn(merchant) -> "transfer"
            else -> "uncategorized"
        }
    }

    private fun remember(id: String): Boolean {
        if (id in recentSet) return false
        recentIds.addLast(id)
        recentSet.add(id)
        while (recentIds.size > MAX_RECENT_IDS) {
            recentSet.remove(recentIds.removeFirst())
        }
        return true
    }

    private data class ParsedPayment(
        val amount: Double,
        val direction: String,
        val merchant: String,
    )

    companion object {
        private const val WECHAT_PACKAGE = "com.tencent.mm"
        private const val MAX_RECENT_IDS = 100
        private val AMOUNT_REGEX = Regex("""(?:￥|¥)?\s*(\d+(?:\.\d{1,2})?)\s*元""")
        private val PAYMENT_KEYWORDS = listOf("微信支付", "支付成功", "已支付", "付款", "收款到账", "已收款")
        private val INCOME_KEYWORDS = listOf("收款到账", "已收款", "收款", "收入")

        private fun sha256(value: String): String {
            val bytes = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.UTF_8))
            return bytes.joinToString("") { "%02x".format(it) }
        }
    }
}
