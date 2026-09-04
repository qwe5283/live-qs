package com.ailife.android.network

import com.ailife.android.generated.VersionedEvent
import com.ailife.android.generated.EventBatchRequest
import com.ailife.android.generated.EventBatchResponse
import com.ailife.android.generated.HeartbeatRequest
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit

class ReportClient(
    private val serverUrl: String,
    private val token: String,
) {
    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()

    private val jsonMediaType = "application/json; charset=utf-8".toMediaType()
    private val json = Json { ignoreUnknownKeys = true }

    /**
     * Uploads one heartbeat as the device current-state projection. The server
     * acknowledges even out-of-order heartbeats without regressing state, so a
     * plain 2xx is the only confirmation the spool needs.
     */
    fun postHeartbeat(heartbeat: HeartbeatRequest): Result<Unit> {
        val request = Request.Builder()
            .url("${serverUrl.trimEnd('/')}/api/v1/heartbeats")
            .addHeader("Authorization", "Bearer $token")
            .post(json.encodeToString(heartbeat).toRequestBody(jsonMediaType))
            .build()

        return try {
            client.newCall(request).execute().use { response ->
                if (response.isSuccessful) Result.success(Unit)
                else Result.failure(IOException("HTTP ${response.code}: ${response.body?.string().orEmpty()}"))
            }
        } catch (e: Exception) {
            Result.failure(e)
        }
    }

    /**
     * Uploads one batch of versioned contract events and returns the per-item
     * acknowledgements. A 2xx with one acknowledgement per submitted event is
     * the confirmation the outbox reasons about; transport and shape failures
     * surface as errors so pending entries are kept for the next pass.
     */
    fun postEventBatch(events: List<VersionedEvent>): Result<EventBatchResponse> {
        if (events.isEmpty()) return Result.success(EventBatchResponse(results = emptyList()))

        val request = Request.Builder()
            .url("${serverUrl.trimEnd('/')}/api/v1/events/batch")
            .addHeader("Authorization", "Bearer $token")
            .post(json.encodeToString(EventBatchRequest(events)).toRequestBody(jsonMediaType))
            .build()

        return try {
            client.newCall(request).execute().use { response ->
                val body = response.body?.string().orEmpty()
                if (!response.isSuccessful) {
                    return Result.failure(IOException("HTTP ${response.code}: $body"))
                }
                Result.success(json.decodeFromString<EventBatchResponse>(body))
            }
        } catch (e: Exception) {
            Result.failure(e)
        }
    }

    fun close() {
        client.dispatcher.executorService.shutdown()
        client.connectionPool.evictAll()
    }
}
