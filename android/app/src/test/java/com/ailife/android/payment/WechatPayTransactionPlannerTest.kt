package com.ailife.android.payment

import com.ailife.android.generated.Category
import com.ailife.android.generated.Direction
import com.ailife.android.generated.EventType
import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.SourceKind
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WechatPayTransactionPlannerTest {
    private val zone = ZoneId.of("Asia/Shanghai")
    private val deviceId = "phone"
    private val installGuid = "2a8f6b34-1f9e-4d0a-9f52-6f3ba0c1d7e8"
    private val postedAt = 1_787_196_363_000L // 2026-08-20T03:26:03Z

    private fun transaction(
        key: String = "0|com.tencent.mm|99|n1",
        postedAtMillis: Long = postedAt,
        amountMinor: Long = 2150L,
        direction: Direction = Direction.EXPENSE,
        merchant: String = "瑞幸咖啡",
        category: Category = Category.FOOD,
    ) = PaymentParseResult.Transaction(
        notificationKey = key,
        postedAtMillis = postedAtMillis,
        amountMinor = amountMinor,
        currency = "CNY",
        direction = direction,
        merchant = merchant,
        category = category,
    )

    private fun plan(
        transactions: List<PaymentParseResult.Transaction>,
        state: Map<String, WechatTransactionState> = emptyMap(),
        nowMillis: Long = postedAt + 2_000,
    ) = WechatPayTransactionPlanner.plan(
        transactions = transactions,
        state = state,
        deviceId = deviceId,
        ownerId = "local",
        installGuid = installGuid,
        nowMillis = nowMillis,
        collectorVersion = "0.3.0",
        zone = zone,
    )

    @Test
    fun transactionCarriesExactAmountDirectionMerchantCategoryAndOccurrenceTime() {
        val event = plan(listOf(transaction())).events.single()

        assertEquals(EventType.PAYMENT_TRANSACTION, event.eventType)
        assertEquals(1L, event.revision)
        assertEquals(FinalizationState.FINAL, event.finalizationState)
        // start_at is the occurrence time; a transaction is instantaneous.
        assertEquals("2026-08-20T03:26:03Z", event.startAt)
        assertNull(event.endAt)
        assertEquals(2150L, event.payload.amount?.value)
        assertEquals("CNY", event.payload.amount?.currency)
        assertEquals(Direction.EXPENSE, event.payload.direction)
        assertEquals("瑞幸咖啡", event.payload.merchant)
        assertEquals(Category.FOOD, event.payload.category)
        assertEquals(false, event.payload.pendingConfirmation)
        assertEquals("android.wechatpay", event.source.kind.value)
        assertTrue(event.source.recordId.startsWith("wechat-notification-"))
        assertEquals(deviceId, event.device.id)
        assertEquals("android", event.device.platform.value)
        assertEquals("local", event.ownerId)
        assertEquals("Asia/Shanghai", event.captureTimezone)
        assertEquals(480L, event.captureOffsetMinutes)
        // Financial facts default to sensitive data.
        assertEquals(PrivacyLevel.SENSITIVE, event.privacyLevel)
        assertEquals("0.3.0", event.provenance.collectorVersion)
        assertEquals(false, event.invalidated)
    }

    @Test
    fun identityIsStableAcrossReplansAndScopedByDeviceInstallAndNotification() {
        val first = WechatPayTransactionPlanner.eventIdFor(deviceId, installGuid, transaction())
        val replanned = WechatPayTransactionPlanner.eventIdFor(deviceId, installGuid, transaction())

        assertEquals(first, replanned)
        assertNotEquals(first, WechatPayTransactionPlanner.eventIdFor(deviceId, installGuid, transaction(key = "0|com.tencent.mm|99|n2")))
        assertNotEquals(first, WechatPayTransactionPlanner.eventIdFor(deviceId, "other-install", transaction()))
        assertNotEquals(first, WechatPayTransactionPlanner.eventIdFor("other-device", installGuid, transaction()))
    }

    @Test
    fun identicalRedeliveryIsSkippedAndOnlyContentChangesRaiseTheRevision() {
        val first = plan(listOf(transaction()))
        val state = first.states

        val replan = plan(listOf(transaction()), state = state)
        assertEquals(0, replan.events.size)
        assertEquals(1, replan.unchangedCount)

        val corrected = plan(listOf(transaction(amountMinor = 2250L)), state = state)
        val event = corrected.events.single()
        assertEquals(first.events.single().eventId, event.eventId)
        assertEquals(2L, event.revision)
        assertEquals(2250L, event.payload.amount?.value)
    }

    @Test
    fun suspectedDuplicateFromAnotherNotificationIsFlaggedPendingConfirmationAndNeverMerged() {
        val first = transaction(key = "0|com.tencent.mm|99|n1", postedAtMillis = postedAt)
        val firstPlan = plan(listOf(first))
        assertEquals(0, firstPlan.suspectedDuplicateCount)

        // A second notification reporting the same payment: same amount,
        // direction, merchant, within the suspicion window.
        val second = transaction(key = "0|com.tencent.mm|99|n2", postedAtMillis = postedAt + 60_000)
        val secondPlan = plan(listOf(second), state = firstPlan.states)

        val event = secondPlan.events.single()
        assertTrue(event.payload.pendingConfirmation == true)
        assertEquals(1, secondPlan.suspectedDuplicateCount)
        assertNotEquals(firstPlan.events.single().eventId, event.eventId)
        // Both observations exist; nothing was merged away.
        assertEquals(1, firstPlan.events.size)
    }

    @Test
    fun similarTransactionsOutsideTheSuspicionMatchAreNotFlagged() {
        val first = transaction()
        val state = plan(listOf(first)).states

        // Different amount, same everything else.
        val differentAmount = plan(listOf(transaction(key = "n2", amountMinor = 3000L)), state = state)
        assertFalse(differentAmount.events.single().payload.pendingConfirmation == true)

        // Same amount but far away in time.
        val muchLater = plan(listOf(transaction(key = "n3", postedAtMillis = postedAt + 60L * 60 * 1000)), state = state)
        assertFalse(muchLater.events.single().payload.pendingConfirmation == true)

        // Income versus expense with the same amount.
        val otherDirection = plan(listOf(transaction(key = "n4", direction = Direction.INCOME)), state = state)
        assertFalse(otherDirection.events.single().payload.pendingConfirmation == true)

        // Different merchant.
        val otherMerchant = plan(listOf(transaction(key = "n5", merchant = "美团外卖")), state = state)
        assertFalse(otherMerchant.events.single().payload.pendingConfirmation == true)
    }

    @Test
    fun eachNotificationKeepsItsOwnReconciliationRecordId() {
        val first = transaction(key = "n1")
        val second = transaction(key = "n2")

        val events = plan(listOf(first, second)).events

        assertEquals(2, events.size)
        assertNotEquals(events[0].source.recordId, events[1].source.recordId)
    }
}
