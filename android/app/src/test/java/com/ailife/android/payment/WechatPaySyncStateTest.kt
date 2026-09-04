package com.ailife.android.payment

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class WechatPaySyncStateTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private fun stateFile(): File = File(temporaryFolder.root, "payment-sync-state.json")

    private fun sampleState(
        eventId: String = "event-1",
        timeMillis: Long = 1_787_196_363_000L,
        revision: Long = 1L,
    ) = WechatTransactionState(
        eventId = eventId,
        recordId = "wechat-notification-$eventId",
        revision = revision,
        fingerprint = "fp-$eventId-$revision",
        timeMillis = timeMillis,
        direction = "expense",
        amountMinor = 2150L,
        merchant = "瑞幸咖啡",
    )

    @Test
    fun mintsADurableInstallGuidOnFirstUseAndKeepsItStable() {
        val first = WechatPaySyncState(stateFile())
        val minted = first.installGuid
        assertTrue(minted.isNotBlank())
        // A fresh instance over the same file observes the same identity epoch.
        assertEquals(minted, WechatPaySyncState(stateFile()).installGuid)

        val wiped = File(temporaryFolder.root, "wiped.json")
        assertNotEquals(minted, WechatPaySyncState(wiped).installGuid)
    }

    @Test
    fun revisionsSurviveProcessRestarts() {
        val state = WechatPaySyncState(stateFile())
        state.record("event-1", sampleState(revision = 3L))
        state.save()

        val reloaded = WechatPaySyncState(stateFile())
        assertEquals(3L, reloaded.transactions.getValue("event-1").revision)
        assertEquals(2150L, reloaded.transactions.getValue("event-1").amountMinor)
        assertEquals("瑞幸咖啡", reloaded.transactions.getValue("event-1").merchant)
    }

    @Test
    fun pruningDropsOnlyTransactionsOlderThanTheHorizon() {
        val state = WechatPaySyncState(stateFile())
        val horizon = 1_755_700_000_000L
        state.record("old", sampleState(eventId = "old", timeMillis = horizon - 1))
        state.record("new", sampleState(eventId = "new", timeMillis = horizon))
        state.prunePostedBefore(horizon)
        state.save()

        val reloaded = WechatPaySyncState(stateFile())
        assertEquals(setOf("new"), reloaded.transactions.keys)
    }

    @Test
    fun corruptedStateStartsANewIdentityEpochInsteadOfForkingRevisions() {
        val file = stateFile()
        file.writeText("not json at all")
        val recovered = WechatPaySyncState(file)
        assertTrue(recovered.transactions.isEmpty())
        assertTrue(recovered.installGuid.isNotBlank())
    }
}
