package com.ailife.android.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

val MintBg = Color(0xFFF7F9F9)
val Card = Color(0xFFFFFFFF)
val Border = Color(0xFFE2E8F0)
val Primary = Color(0xFF48BB78)
val Secondary = Color(0xFF63B3ED)
val Accent = Color(0xFFED8936)
val TextMain = Color(0xFF1A202C)
val TextMuted = Color(0xFF718096)

private val AiLifeColorScheme = lightColorScheme(
    primary = Primary,
    secondary = Secondary,
    tertiary = Accent,
    background = MintBg,
    surface = Card,
    onPrimary = Color.White,
    onSecondary = Color.White,
    onTertiary = Color.White,
    onBackground = TextMain,
    onSurface = TextMain,
    outline = Border,
    surfaceVariant = Color(0xFFEDF2F7),
    onSurfaceVariant = TextMuted,
)

private val AiLifeTypography = Typography(
    headlineLarge = TextStyle(
        fontWeight = FontWeight.Bold,
        fontSize = 24.sp,
        color = TextMain,
    ),
    headlineMedium = TextStyle(
        fontWeight = FontWeight.SemiBold,
        fontSize = 20.sp,
        color = TextMain,
    ),
    titleMedium = TextStyle(
        fontWeight = FontWeight.Medium,
        fontSize = 16.sp,
        color = TextMain,
    ),
    bodyLarge = TextStyle(
        fontSize = 16.sp,
        color = TextMain,
    ),
    bodyMedium = TextStyle(
        fontSize = 14.sp,
        color = TextMain,
    ),
    bodySmall = TextStyle(
        fontSize = 12.sp,
        color = TextMuted,
    ),
    labelLarge = TextStyle(
        fontWeight = FontWeight.Medium,
        fontSize = 14.sp,
        fontFamily = FontFamily.Monospace,
        color = TextMain,
    ),
)

@Composable
fun AiLifeTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = AiLifeColorScheme,
        typography = AiLifeTypography,
        content = content,
    )
}
