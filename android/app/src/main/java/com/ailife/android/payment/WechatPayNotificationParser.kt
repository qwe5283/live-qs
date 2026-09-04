package com.ailife.android.payment

import com.ailife.android.generated.Category
import com.ailife.android.generated.Direction

/**
 * Outcome of parsing one notification into minimal structured payment facts.
 *
 * - [Transaction]: the notification described a payment and every contract
 *   field could be extracted exactly;
 * - [NotPayment]: the notification is unrelated to payments (no payment
 *   keyword) and is ignored entirely;
 * - [Failure]: the notification mentions payment keywords but no exact amount
 *   could be extracted — it is kept ONLY in the local failure queue for
 *   diagnosis and never uploaded, never guessed.
 */
sealed class PaymentParseResult {
    data class Transaction(
        val notificationKey: String,
        val postedAtMillis: Long,
        val amountMinor: Long,
        val currency: String,
        val direction: Direction,
        val merchant: String,
        val category: Category,
    ) : PaymentParseResult()

    data object NotPayment : PaymentParseResult()

    data class Failure(val reason: String) : PaymentParseResult()
}

/**
 * Extracts minimal structured transaction facts from WeChat payment
 * notifications on-device. The extraction rules (keyword gates, amount
 * pattern, direction keywords, merchant cleanup, category table) preserve the
 * proven legacy collector verbatim; only the output path changed — the result
 * is typed contract fields, and notification text is never part of it.
 *
 * This object is pure: strings in, parse result out. Notification inputs stay
 * on the device; callers must never persist them outside the local failure
 * queue for [Failure] diagnosis.
 */
object WechatPayNotificationParser {
    /**
     * Parses one notification. The title, text, and expanded text are joined
     * exactly like the legacy collector before the keyword and amount rules
     * run.
     */
    fun parse(
        notificationKey: String,
        postedAtMillis: Long,
        title: String,
        text: String,
        bigText: String,
    ): PaymentParseResult {
        val content = listOf(title, text, bigText).filter { it.isNotBlank() }.joinToString(" ")
        if (content.isBlank()) return PaymentParseResult.NotPayment
        if (PAYMENT_KEYWORDS.none { content.contains(it) }) return PaymentParseResult.NotPayment

        val amountMatch = AMOUNT_REGEX.find(content)?.groups?.get(1)?.value
            ?: return PaymentParseResult.Failure("missing_amount")
        val amountMinor = toMinorUnits(amountMatch)
            ?: return PaymentParseResult.Failure("unparseable_amount")
        if (amountMinor <= 0L) return PaymentParseResult.Failure("non_positive_amount")

        val direction = if (INCOME_KEYWORDS.any { content.contains(it) }) Direction.INCOME else Direction.EXPENSE
        val merchant = extractMerchant(title, text, direction)
        return PaymentParseResult.Transaction(
            notificationKey = notificationKey,
            postedAtMillis = postedAtMillis,
            amountMinor = amountMinor,
            currency = CURRENCY_CNY,
            direction = direction,
            merchant = merchant,
            category = categorize(merchant),
        )
    }

    /**
     * Converts a decimal amount string (one or two fraction digits, as
     * captured by [AMOUNT_REGEX]) into exact minor units with string math, so
     * no floating-point value ever touches money.
     */
    private fun toMinorUnits(amount: String): Long? {
        val parts = amount.split(".")
        val whole = parts[0].toLongOrNull() ?: return null
        val fraction = when {
            parts.size == 1 -> "00"
            parts[1].length == 1 -> "${parts[1]}0"
            parts[1].length == 2 -> parts[1]
            else -> return null
        }
        val minor = fraction.toLongOrNull() ?: return null
        return whole * 100L + minor
    }

    private fun extractMerchant(title: String, text: String, direction: Direction): String {
        val candidate = if (direction == Direction.INCOME) title else text.ifBlank { title }
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

    private fun categorize(merchant: String): Category = when {
        Regex("美团|饿了么|大众点评|餐|饭|食堂|咖啡|奶茶|茶饮|超市|便利店|盒马|麦当劳|肯德基|必胜客|星巴克|瑞幸|喜茶|奈雪|kfc|starbucks|luckin|coffee", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> Category.FOOD
        Regex("滴滴|小拉|打车|地铁|公交|铁路|12306|机票|航旅|高德|停车|加油|充电|高速|etc|taxi|metro", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> Category.TRANSPORT
        Regex("京东|淘宝|天猫|拼多多|抖音商城|小红书|唯品会|得物|苏宁|商城|购物|数码|服饰|家居").containsMatchIn(merchant) -> Category.SHOPPING
        Regex("水费|电费|燃气|话费|宽带|物业|房租|租金|保险|税|账单|生活缴费").containsMatchIn(merchant) -> Category.BILLS
        Regex("医院|药|医保|体检|诊所|口腔|牙|挂号|pharmacy|clinic|hospital", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> Category.HEALTH
        Regex("课程|教育|培训|学校|大学|考试|书店|图书|知识付费|得到|极客时间|coursera|udemy", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> Category.EDUCATION
        Regex("电影|影院|游戏|音乐|会员|视频|优酷|腾讯视频|爱奇艺|哔哩|bilibili|steam|spotify|netflix", RegexOption.IGNORE_CASE).containsMatchIn(merchant) -> Category.ENTERTAINMENT
        Regex("转账|红包").containsMatchIn(merchant) -> Category.TRANSFER
        else -> Category.UNCATEGORIZED
    }

    internal val AMOUNT_REGEX = Regex("""(?:￥|¥)?\s*(\d+(?:\.\d{1,2})?)\s*元""")
    private val PAYMENT_KEYWORDS = listOf("微信支付", "支付成功", "已支付", "付款", "收款到账", "已收款")
    private val INCOME_KEYWORDS = listOf("收款到账", "已收款", "收款", "收入")
    private const val CURRENCY_CNY = "CNY"
}
