package com.ailife.android.network

import com.ailife.android.data.model.LifeEvent
import com.ailife.android.data.model.LifeHeartbeat
import com.ailife.android.data.model.toJsonObject
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.time.Instant
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

    fun postForegroundHeartbeat(
        deviceId: String,
        packageName: String,
        appName: String,
        heartbeatIntervalMs: Long,
    ): Result<Unit> {
        return postHeartbeat(
            foregroundHeartbeat(
                deviceId = deviceId,
                packageName = packageName,
                appName = appName,
                heartbeatIntervalMs = heartbeatIntervalMs,
            ),
        )
    }

    fun postHeartbeat(heartbeat: LifeHeartbeat): Result<Unit> {
        val request = Request.Builder()
            .url("${serverUrl.trimEnd('/')}/api/v1/ingest/heartbeat")
            .addHeader("Authorization", "Bearer $token")
            .post(heartbeat.toJsonObject().toString().toRequestBody(jsonMediaType))
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

    fun postEvents(events: List<LifeEvent>): Result<Unit> {
        if (events.isEmpty()) return Result.success(Unit)

        val body = JSONObject().apply {
            put("events", JSONArray().apply {
                for (event in events) {
                    put(event.toJsonObject())
                }
            })
        }

        val request = Request.Builder()
            .url("${serverUrl.trimEnd('/')}/api/v1/ingest/events")
            .addHeader("Authorization", "Bearer $token")
            .post(body.toString().toRequestBody(jsonMediaType))
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

    fun close() {
        client.dispatcher.executorService.shutdown()
        client.connectionPool.evictAll()
    }

    companion object {
        fun foregroundHeartbeat(
            deviceId: String,
            packageName: String,
            appName: String,
            heartbeatIntervalMs: Long,
        ): LifeHeartbeat {
            return LifeHeartbeat(
                bucket = "android:$deviceId:foreground",
                type = "app.foreground",
                timestamp = Instant.now().toString(),
                heartbeatIntervalMs = heartbeatIntervalMs,
                data = mapOf(
                    "app_id" to packageName,
                    "app_name" to appName,
                    "is_afk" to false,
                    "source" to "accessibility",
                ),
            )
        }
    }
}
