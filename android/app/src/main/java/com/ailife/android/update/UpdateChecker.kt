package com.ailife.android.update

import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request

/**
 * V1 update check for the android component: fetch the component's own
 * manifest, evaluate it against the running version, and persist the
 * diagnosable outcome. Notify-only by design — the app never downloads or
 * installs the APK itself; the Owner opens the verified download URL and
 * installs manually, so no unknown-sources flow exists in V1.
 */
class UpdateChecker(
    private val stateStore: UpdateCheckStateStore,
    private val fetchManifest: suspend (String) -> String,
    private val currentVersion: () -> String,
    private val nowMillis: () -> Long = System::currentTimeMillis,
    private val component: String = UpdateComponents.ANDROID,
) {
    suspend fun checkOnce(manifestUrl: String): UpdateCheckSnapshot {
        val snapshot = checkInternal(manifestUrl)
        stateStore.write(snapshot)
        return snapshot
    }

    private suspend fun checkInternal(manifestUrl: String): UpdateCheckSnapshot {
        val now = nowMillis()
        val manifestText = try {
            fetchManifest(manifestUrl)
        } catch (_: Exception) {
            return UpdateCheckSnapshot(
                state = UpdateCheckState.FAILED,
                lastCheckAtMillis = now,
                errorCode = UpdateCodes.MANIFEST_FETCH_FAILED,
                errorMessage = "无法获取更新清单。",
            )
        }
        val manifest = parseUpdateManifest(manifestText) ?: return UpdateCheckSnapshot(
            state = UpdateCheckState.FAILED,
            lastCheckAtMillis = now,
            errorCode = UpdateCodes.MANIFEST_PARSE_FAILED,
            errorMessage = "更新清单缺失字段、格式错误或含有未知字段。",
        )
        val decision = evaluateUpdate(component, currentVersion(), manifest)
        return when (decision.kind) {
            UpdateDecisionKind.UP_TO_DATE -> UpdateCheckSnapshot(
                state = UpdateCheckState.UP_TO_DATE,
                lastCheckAtMillis = now,
                errorCode = decision.code,
            )
            UpdateDecisionKind.REFUSE -> UpdateCheckSnapshot(
                state = if (decision.code == UpdateCodes.MIN_COMPATIBLE_NOT_MET) {
                    UpdateCheckState.INCOMPATIBLE
                } else {
                    UpdateCheckState.FAILED
                },
                availableVersion = decision.version,
                releasedAt = decision.releasedAt,
                downloadUrl = decision.downloadUrl,
                lastCheckAtMillis = now,
                errorCode = decision.code,
                errorMessage = decision.detail,
            )
            UpdateDecisionKind.AVAILABLE -> UpdateCheckSnapshot(
                state = UpdateCheckState.AVAILABLE,
                availableVersion = decision.version,
                releasedAt = decision.releasedAt,
                downloadUrl = decision.downloadUrl,
                lastCheckAtMillis = now,
                errorCode = decision.code,
            )
        }
    }

    companion object {
        /** Fetches the manifest over plain HTTP; only a 2xx body is returned. */
        suspend fun fetchOverHttp(url: String): String = withContext(Dispatchers.IO) {
            val client = OkHttpClient.Builder()
                .connectTimeout(10, TimeUnit.SECONDS)
                .readTimeout(20, TimeUnit.SECONDS)
                .build()
            val request = Request.Builder().url(url).build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
                response.body?.string() ?: throw IOException("empty update manifest response")
            }
        }
    }
}
