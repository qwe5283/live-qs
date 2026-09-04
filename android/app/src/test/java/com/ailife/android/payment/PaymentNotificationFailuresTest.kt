package com.ailife.android.payment

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PaymentNotificationFailuresTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun failuresFile(): File = File(temporaryFolder.root, "payment-notification-failures.ndjson")

    private fun failure(fingerprint: String, reason: String = "missing_amount") = PaymentNotificationFailure(
        sourceFingerprint = fingerprint,
        reason = reason,
        postedAt = "2026-08-20T03:26:03Z",
        title = "微信支付",
        text = "支付成功",
        recordedAt = "2026-08-20T03:26:05Z",
    )

    @Test
    fun recordedFailuresRoundTripForLocalDiagnosis() {
        val failures = PaymentNotificationFailures(failuresFile())
        failures.record(failure("n1"))
        failures.record(failure("n2", reason = "non_positive_amount"))

        val stored = failures.readAll()
        assertEquals(2, stored.size)
        assertEquals("n1", stored[0].sourceFingerprint)
        assertEquals("non_positive_amount", stored[1].reason)
        // The raw text stays local for diagnosis; nothing here is ever uploaded.
        assertEquals("微信支付", stored[0].title)
    }

    @Test
    fun theSameNotificationIsRecordedOnlyOnce() {
        val failures = PaymentNotificationFailures(failuresFile())
        failures.record(failure("n1"))
        failures.record(failure("n1"))

        assertEquals(1, failures.size())
    }

    @Test
    fun theQueueIsCappedKeepingTheNewestEntries() {
        val failures = PaymentNotificationFailures(failuresFile(), capacity = 3)
        for (index in 1..5) {
            failures.record(failure("n$index"))
        }

        val stored = failures.readAll()
        assertEquals(3, stored.size)
        assertEquals(listOf("n3", "n4", "n5"), stored.map { it.sourceFingerprint })
    }

    @Test
    fun anEmptyQueueHasNoFile() {
        val failures = PaymentNotificationFailures(failuresFile())
        assertEquals(0, failures.size())
        assertTrue(!failuresFile().exists() || failuresFile().length() == 0L)
    }
}
