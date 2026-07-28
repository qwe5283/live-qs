package com.ailife.android.network

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request

suspend fun testServerReachability(serverUrl: String): Boolean = withContext(Dispatchers.IO) {
    val url = serverUrl.trim().trimEnd('/')
    if (url.isBlank()) return@withContext false
    val client = OkHttpClient.Builder().build()
    val request = Request.Builder()
        .url("$url/health")
        .get()
        .build()
    runCatching {
        client.newCall(request).execute().use { response -> response.isSuccessful }
    }.getOrDefault(false)
}
