package com.ailife.android.ui

import android.app.Activity
import android.os.Bundle
import android.widget.TextView

class PermissionRationaleActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(TextView(this).apply {
            text = "AI Life reads steps, heart rate, and sleep records for private agent context summaries."
            textSize = 18f
            setPadding(48, 48, 48, 48)
        })
    }
}
