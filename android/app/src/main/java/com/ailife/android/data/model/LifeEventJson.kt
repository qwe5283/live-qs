package com.ailife.android.data.model

import org.json.JSONObject

fun LifeEvent.toJsonObject(): JSONObject {
    return JSONObject().apply {
        put("idempotency_key", idempotencyKey)
        put("bucket", bucket)
        put("type", type)
        put("start_at", startAt)
        endAt?.let { put("end_at", it) }
        value?.let { put("value", it) }
        unit?.let { put("unit", it) }
        put("privacy_level", privacyLevel)
        put("data", JSONObject(data))
    }
}

fun lifeEventFromJsonObject(json: JSONObject): LifeEvent {
    val dataJson = json.optJSONObject("data") ?: JSONObject()
    return LifeEvent(
        idempotencyKey = json.getString("idempotency_key"),
        bucket = json.getString("bucket"),
        type = json.getString("type"),
        startAt = json.getString("start_at"),
        endAt = json.optString("end_at").takeIf { it.isNotBlank() },
        value = if (json.has("value")) json.optDouble("value") else null,
        unit = json.optString("unit").takeIf { it.isNotBlank() },
        privacyLevel = json.optString("privacy_level", "normal"),
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
