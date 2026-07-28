package com.ailife.android.data.model

import org.json.JSONObject

fun LifeHeartbeat.toJsonObject(): JSONObject {
    return JSONObject().apply {
        put("bucket", bucket)
        put("type", type)
        put("timestamp", timestamp)
        put("heartbeat_interval_ms", heartbeatIntervalMs)
        put("data", JSONObject(data))
    }
}

fun lifeHeartbeatFromJsonObject(json: JSONObject): LifeHeartbeat {
    val dataJson = json.optJSONObject("data") ?: JSONObject()
    return LifeHeartbeat(
        bucket = json.getString("bucket"),
        type = json.getString("type"),
        timestamp = json.getString("timestamp"),
        heartbeatIntervalMs = json.optLong("heartbeat_interval_ms", 10_000L),
        data = dataJson.toMap(),
    )
}

private fun JSONObject.toMap(): Map<String, Any?> {
    val result = mutableMapOf<String, Any?>()
    val keys = keys()
    while (keys.hasNext()) {
        val key = keys.next()
        result[key] = when (val value = opt(key)) {
            JSONObject.NULL -> null
            is JSONObject -> value.toMap()
            else -> value
        }
    }
    return result
}
