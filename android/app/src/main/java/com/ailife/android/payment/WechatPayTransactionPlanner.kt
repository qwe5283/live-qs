package com.ailife.android.payment

import com.ailife.android.generated.Amount
import com.ailife.android.generated.Device
import com.ailife.android.generated.Direction
import com.ailife.android.generated.EventType
import com.ailife.android.generated.FinalizationState
import com.ailife.android.generated.Payload
import com.ailife.android.generated.Platform
import com.ailife.android.generated.PrivacyLevel
import com.ailife.android.generated.Provenance
import com.ailife.android.generated.Source
import com.ailife.android.generated.SourceKind
import com.ailife.android.generated.VersionedEvent
import com.ailife.android.identity.UuidNameIds
import java.security.MessageDigest
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** Outcome of one planning pass over parsed payment notifications. */
data class PaymentEventPlan(
    val events: List<VersionedEvent>,
    val states: Map<String, WechatTransactionState>,
    /** Notifications skipped because an identical fingerprint was already reported. */
    val unchangedCount: Int,
    /** Notifications flagged pending-confirmation as suspected duplicates. */
    val suspectedDuplicateCount: Int,
)

/**
 * Turns parsed payment notifications into contract events with stable
 * identities and monotonic revisions:
 *
 * - identity: UUIDv5 over the event type + device id + install GUID + the
 *   notification key + post time. Repeated deliveries of the same
 *   notification therefore address one logical event and the server answers
 *   `duplicate` — retries never double-book.
 * - the notification is the *source record*, not the payment network
 *   transaction: `source.record_id` is the notification fingerprint. Two
 *   different notifications describing the same payment get different event
 *   identities, so a suspected duplicate is flagged
 *   `pending_confirmation` (matching amount, direction, merchant, and a
 *   nearby occurrence time) and kept as a distinct observation — never merged
 *   on amount and time alone.
 * - revision 1 for the first report of a notification; only a changed
 *   content fingerprint raises the revision; an unchanged notification is
 *   skipped (redelivery is the outbox's job).
 * - every event is instantaneous (`start_at` is the occurrence time, no
 *   `end_at`), finalized, and defaults to the sensitive privacy level.
 *
 * The planner is pure: transactions and state in, plan out. Durability
 * belongs to [WechatPaySyncState]; notification access belongs to the
 * notification listener service.
 */
object WechatPayTransactionPlanner {
    fun plan(
        transactions: List<PaymentParseResult.Transaction>,
        state: Map<String, WechatTransactionState>,
        deviceId: String,
        ownerId: String,
        installGuid: String,
        nowMillis: Long,
        collectorVersion: String,
        zone: ZoneId,
    ): PaymentEventPlan {
        val events = mutableListOf<VersionedEvent>()
        val states = mutableMapOf<String, WechatTransactionState>()
        var unchanged = 0
        var suspectedDuplicates = 0

        for (transaction in transactions.sortedWith(compareBy({ it.postedAtMillis }, { it.notificationKey }))) {
            val eventId = eventIdFor(deviceId, installGuid, transaction)
            val fingerprint = fingerprintOf(transaction)
            val previous = state[eventId.toString()]
            if (previous != null && previous.fingerprint == fingerprint) {
                states[eventId.toString()] = previous
                unchanged += 1
                continue
            }

            // A different notification that looks like the same payment is
            // flagged for Owner confirmation instead of being merged away.
            val suspectedDuplicate = state.values.any { candidate ->
                candidate.eventId != eventId.toString() &&
                    candidate.direction == transaction.direction.value &&
                    candidate.amountMinor == transaction.amountMinor &&
                    candidate.merchant == transaction.merchant &&
                    Math.abs(candidate.timeMillis - transaction.postedAtMillis) <= DUPLICATE_WINDOW_MS
            }
            if (suspectedDuplicate) suspectedDuplicates += 1

            val revision = (previous?.revision ?: 0L) + 1L
            val recordId = sourceRecordId(transaction)
            val startInstant = Instant.ofEpochMilli(transaction.postedAtMillis)
            events.add(
                VersionedEvent(
                    eventType = EventType.PAYMENT_TRANSACTION,
                    payload = Payload(
                        amount = Amount(value = transaction.amountMinor, currency = transaction.currency),
                        category = transaction.category,
                        direction = transaction.direction,
                        merchant = transaction.merchant,
                        pendingConfirmation = suspectedDuplicate,
                    ),
                    privacyLevel = PrivacyLevel.SENSITIVE,
                    schemaVersion = 1,
                    source = Source(kind = SourceKind.ANDROID_WECHATPAY, recordId = recordId),
                    captureOffsetMinutes = zone.rules.getOffset(startInstant).totalSeconds / 60L,
                    captureTimezone = zone.id,
                    device = Device(id = deviceId, platform = Platform.ANDROID),
                    eventId = eventId.toString(),
                    finalizationState = FinalizationState.FINAL,
                    invalidated = false,
                    ownerId = ownerId,
                    provenance = Provenance(
                        collectorVersion = collectorVersion,
                        observedAt = Instant.ofEpochMilli(nowMillis).toString(),
                    ),
                    revision = revision,
                    startAt = startInstant.toString(),
                ),
            )
            states[eventId.toString()] = WechatTransactionState(
                eventId = eventId.toString(),
                recordId = recordId,
                revision = revision,
                fingerprint = fingerprint,
                timeMillis = transaction.postedAtMillis,
                direction = transaction.direction.value,
                amountMinor = transaction.amountMinor,
                merchant = transaction.merchant,
            )
        }

        return PaymentEventPlan(
            events = events,
            states = states,
            unchangedCount = unchanged,
            suspectedDuplicateCount = suspectedDuplicates,
        )
    }

    /**
     * Stable identity for one logical event: the same notification redelivered
     * later yields the same event id regardless of parse timing, while a
     * reinstall regenerates the install GUID and starts a new identity epoch.
     */
    fun eventIdFor(deviceId: String, installGuid: String, transaction: PaymentParseResult.Transaction): UUID =
        UuidNameIds.forRecord(
            "payment.transaction",
            deviceId,
            installGuid,
            transaction.notificationKey,
            transaction.postedAtMillis.toString(),
        )

    /**
     * Reconciliation anchor for the source record: a stable fingerprint of the
     * notification itself (never of its text), so one parsed notification maps
     * to exactly one server fact.
     */
    fun sourceRecordId(transaction: PaymentParseResult.Transaction): String =
        "wechat-notification-" + sha256("${transaction.notificationKey}|${transaction.postedAtMillis}").take(16)

    /** Content fingerprint of the extracted facts; a change raises the revision. */
    private fun fingerprintOf(transaction: PaymentParseResult.Transaction): String =
        sha256(
            listOf(
                transaction.notificationKey,
                transaction.postedAtMillis.toString(),
                transaction.amountMinor.toString(),
                transaction.currency,
                transaction.direction.value,
                transaction.merchant,
                transaction.category.value,
            ).joinToString("|"),
        )

    private fun sha256(value: String): String {
        val bytes = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.UTF_8))
        return bytes.joinToString("") { "%02x".format(it) }
    }

    private const val DUPLICATE_WINDOW_MS = 15L * 60 * 1000
}
