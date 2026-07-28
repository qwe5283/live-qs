package com.ailife.android.data.model

data class LifeEvent(
    val idempotencyKey: String,
    val bucket: String,
    val type: String,
    val startAt: String,
    val endAt: String? = null,
    val value: Double? = null,
    val unit: String? = null,
    val privacyLevel: String = "normal",
    val data: Map<String, Any?> = emptyMap(),
)
