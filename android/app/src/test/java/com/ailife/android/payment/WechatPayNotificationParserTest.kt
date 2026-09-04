package com.ailife.android.payment

import com.ailife.android.generated.Category
import com.ailife.android.generated.Direction
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class WechatPayNotificationParserTest {
    private val key = "0|com.tencent.mm|99|wechat-notify-1"
    private val postedAt = 1_755_699_963_000L

    @Test
    fun parsesAnExpenseNotificationIntoExactContractFacts() {
        val result = WechatPayNotificationParser.parse(
            notificationKey = key,
            postedAtMillis = postedAt,
            title = "微信支付",
            text = "向瑞幸咖啡付款21.50元",
            bigText = "",
        )

        val transaction = result as PaymentParseResult.Transaction
        assertEquals(key, transaction.notificationKey)
        assertEquals(postedAt, transaction.postedAtMillis)
        // 21.50 CNY is exactly 2150 fen: money is integer minor units, never a float.
        assertEquals(2150L, transaction.amountMinor)
        assertEquals("CNY", transaction.currency)
        assertEquals(Direction.EXPENSE, transaction.direction)
        assertEquals("向瑞幸咖啡付款", transaction.merchant)
        assertEquals(Category.FOOD, transaction.category)
    }

    @Test
    fun parsesIncomeNotificationsByTheLegacyIncomeKeywords() {
        val result = WechatPayNotificationParser.parse(
            notificationKey = key,
            postedAtMillis = postedAt,
            title = "微信支付",
            text = "已收款100.00元",
            bigText = "",
        )

        val transaction = result as PaymentParseResult.Transaction
        assertEquals(Direction.INCOME, transaction.direction)
        assertEquals(10_000L, transaction.amountMinor)
        // The legacy income extraction uses the title, which here reduces to
        // the approved fallback label.
        assertEquals("WeChat Pay", transaction.merchant)
        assertEquals(Category.UNCATEGORIZED, transaction.category)
    }

    @Test
    fun keepsAmountsExactAcrossFractionShapes() {
        fun amountOf(text: String): Long =
            (WechatPayNotificationParser.parse(key, postedAt, "微信支付", text, "")
                as PaymentParseResult.Transaction).amountMinor

        assertEquals(1230L, amountOf("付款12.3元"))
        assertEquals(1200L, amountOf("付款12元"))
        assertEquals(5L, amountOf("付款0.05元"))
        assertEquals(100_000_000L, amountOf("付款1000000元"))
    }

    @Test
    fun ignoresNotificationsWithoutPaymentKeywords() {
        val result = WechatPayNotificationParser.parse(key, postedAt, "张三", "晚上一起吃饭吗？", "")

        assertEquals(PaymentParseResult.NotPayment, result)
    }

    @Test
    fun blankContentIsIgnoredRatherThanFailing() {
        assertEquals(PaymentParseResult.NotPayment, WechatPayNotificationParser.parse(key, postedAt, "", "", ""))
    }

    @Test
    fun paymentKeywordsWithoutAnAmountFailWithoutGuessing() {
        val noAmount = WechatPayNotificationParser.parse(key, postedAt, "微信支付", "支付成功", "")
        assertTrue(noAmount is PaymentParseResult.Failure)
        assertEquals("missing_amount", (noAmount as PaymentParseResult.Failure).reason)

        val zeroAmount = WechatPayNotificationParser.parse(key, postedAt, "微信支付", "付款0元", "")
        assertEquals("non_positive_amount", (zeroAmount as PaymentParseResult.Failure).reason)
    }

    @Test
    fun categoriesCoverTheLegacyRuleTable() {
        fun categoryOf(merchant: String): Category =
            (WechatPayNotificationParser.parse(key, postedAt, "微信支付", "向${merchant}付款1.00元", "")
                as PaymentParseResult.Transaction).category

        assertEquals(Category.TRANSPORT, categoryOf("滴滴出行"))
        assertEquals(Category.SHOPPING, categoryOf("京东商城"))
        assertEquals(Category.BILLS, categoryOf("电费"))
        assertEquals(Category.ENTERTAINMENT, categoryOf("爱奇艺会员"))
        assertEquals(Category.UNCATEGORIZED, categoryOf("某某贸易"))
    }
}
