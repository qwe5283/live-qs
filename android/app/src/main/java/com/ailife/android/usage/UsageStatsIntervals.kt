package com.ailife.android.usage

/**
 * One raw UsageStats transition relevant to foreground-session pairing.
 * Plain data (no Android types) so session reconstruction is JVM-testable.
 */
data class UsageTransition(
    val packageName: String,
    val timestampMillis: Long,
    val isResume: Boolean,
)

/**
 * One reconstructed per-package foreground session. [endMillis] is `null`
 * while the session is still foreground at the end of the queried window:
 * its true end does not exist yet, so the uploader reports revision
 * checkpoints until a closing transition arrives.
 */
data class UsageInterval(
    val packageName: String,
    val startMillis: Long,
    val endMillis: Long?,
)

/**
 * Pairs UsageStats resume/pause transitions into per-package foreground
 * sessions, the same pairing the system applies when it computes per-app
 * screen time:
 *
 * - a resume while another session is open closes that session at the same
 *   instant (packages switch hands; time is conserved, never double counted);
 * - a pause for a package with no open session is ignored — its session
 *   start predates the queried window and was reported by an earlier pass;
 * - a resume for the already-foreground package is ignored (duplicate);
 * - a session still foreground at the window end stays open.
 */
object UsageStatsIntervals {
    fun build(transitions: List<UsageTransition>): List<UsageInterval> {
        val intervals = mutableListOf<UsageInterval>()
        var activePackage: String? = null
        var activeStartMillis = 0L

        fun closeActive(endMillis: Long) {
            val packageName = activePackage ?: return
            if (endMillis > activeStartMillis) {
                intervals.add(UsageInterval(packageName, activeStartMillis, endMillis))
            }
            activePackage = null
        }

        for (transition in transitions.sortedBy { it.timestampMillis }) {
            val packageName = transition.packageName.takeIf { it.isNotBlank() } ?: continue
            if (transition.isResume) {
                if (activePackage == packageName) continue
                closeActive(transition.timestampMillis)
                activePackage = packageName
                activeStartMillis = transition.timestampMillis
            } else if (activePackage == packageName) {
                closeActive(transition.timestampMillis)
            }
        }

        if (activePackage != null) {
            intervals.add(UsageInterval(activePackage!!, activeStartMillis, null))
        }
        return intervals
    }
}
