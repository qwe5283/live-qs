package com.ailife.android.data.model

data class LifeHeartbeat(
    val bucket: String,
    val type: String,
    val timestamp: String,
    val heartbeatIntervalMs: Long,
    val data: Map<String, Any?> = emptyMap(),
)
