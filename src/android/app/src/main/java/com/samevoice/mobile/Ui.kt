package com.samevoice.mobile

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView

object Palette {
    const val BG = 0xFF080B13.toInt()
    const val CARD = 0xFF111624.toInt()
    const val CARD_SOFT = 0xFF0E1421.toInt()
    const val BORDER = 0xFF222A3D.toInt()
    const val BORDER_SOFT = 0xFF1D2637.toInt()
    const val TEXT = 0xFFF4F7FF.toInt()
    const val SECONDARY = 0xFF8C96AC.toInt()
    const val MUTED = 0xFF5D6880.toInt()
    const val MINT = 0xFF71EBC4.toInt()
    const val VIOLET = 0xFF8E9CFF.toInt()
    const val WARN = 0xFFFFBF69.toInt()
    const val DANGER = 0xFFFF7C8C.toInt()
    const val INPUT = 0xFF0C111D.toInt()
    const val BUTTON_BG = 0xFF182031.toInt()
}

fun Context.dp(value: Number): Int = (value.toFloat() * resources.displayMetrics.density).toInt()

fun Context.roundedBackground(
    color: Int,
    radiusDp: Number = 16,
    strokeColor: Int? = null,
    strokeDp: Number = 1
): GradientDrawable = GradientDrawable().apply {
    shape = GradientDrawable.RECTANGLE
    setColor(color)
    cornerRadius = this@roundedBackground.dp(radiusDp).toFloat()
    if (strokeColor != null) setStroke(this@roundedBackground.dp(strokeDp), strokeColor)
}

fun Context.gradientBackground(
    colors: IntArray,
    orientation: GradientDrawable.Orientation = GradientDrawable.Orientation.TL_BR,
    radiusDp: Number = 20,
    strokeColor: Int? = null
): GradientDrawable = GradientDrawable(orientation, colors).apply {
    shape = GradientDrawable.RECTANGLE
    cornerRadius = this@gradientBackground.dp(radiusDp).toFloat()
    if (strokeColor != null) setStroke(this@gradientBackground.dp(1), strokeColor)
}

fun ripple(background: android.graphics.drawable.Drawable, color: Int = 0x33FFFFFF): RippleDrawable =
    RippleDrawable(ColorStateList.valueOf(color), background, null)

fun Context.text(
    value: CharSequence,
    sizeSp: Float = 12f,
    color: Int = Palette.TEXT,
    medium: Boolean = false,
    bold: Boolean = false
): TextView = TextView(this).apply {
    text = value
    setTextColor(color)
    textSize = sizeSp
    includeFontPadding = false
    typeface = when {
        bold -> Typeface.create("sans-serif", Typeface.BOLD)
        medium -> Typeface.create("sans-serif-medium", Typeface.NORMAL)
        else -> Typeface.create("sans-serif", Typeface.NORMAL)
    }
    setLineSpacing(0f, 1.12f)
}

fun Context.primaryButton(label: CharSequence): TextView = text(label, 14f, 0xFF07120F.toInt(), bold = true).apply {
    gravity = Gravity.CENTER
    isClickable = true
    isFocusable = true
    minHeight = dp(50)
    setPadding(dp(18), dp(14), dp(18), dp(14))
    background = ripple(
        gradientBackground(
            intArrayOf(0xFF7DF3CD.toInt(), 0xFF91B8FF.toInt()),
            radiusDp = 14
        )
    )
    layoutParams = LinearLayout.LayoutParams(
        LinearLayout.LayoutParams.MATCH_PARENT,
        LinearLayout.LayoutParams.WRAP_CONTENT
    )
}

fun Context.secondaryButton(label: CharSequence): TextView = text(label, 12.5f, 0xFFE5ECFA.toInt(), medium = true).apply {
    gravity = Gravity.CENTER
    isClickable = true
    isFocusable = true
    minHeight = dp(44)
    setPadding(dp(16), dp(11), dp(16), dp(11))
    background = ripple(roundedBackground(Palette.BUTTON_BG, 12, 0xFF2A344B.toInt()))
    layoutParams = LinearLayout.LayoutParams(
        LinearLayout.LayoutParams.WRAP_CONTENT,
        LinearLayout.LayoutParams.WRAP_CONTENT
    )
}

fun Context.iconButton(label: CharSequence): TextView = text(label, 17f, 0xFFC9D2E4.toInt()).apply {
    gravity = Gravity.CENTER
    isClickable = true
    isFocusable = true
    background = ripple(roundedBackground(Palette.BUTTON_BG, 10, 0xFF2A344B.toInt()))
    layoutParams = LinearLayout.LayoutParams(dp(34), dp(34))
}

fun Context.pill(label: CharSequence, textColor: Int = Palette.SECONDARY, background: Int = 0xFF20293B.toInt()): TextView =
    text(label, 10f, textColor, medium = true).apply {
        gravity = Gravity.CENTER
        setPadding(dp(9), dp(5), dp(9), dp(5))
        this.background = roundedBackground(background, 99)
    }

fun Context.card(background: android.graphics.drawable.Drawable? = null, padding: Number = 18): LinearLayout =
    LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(padding), dp(padding), dp(padding), dp(padding))
        this.background = background ?: roundedBackground(Palette.CARD, 20, Palette.BORDER)
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT
        )
    }

fun Context.divider(): View = View(this).apply {
    setBackgroundColor(0xFF1E2637.toInt())
    layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1)).apply {
        topMargin = dp(13)
        bottomMargin = dp(13)
    }
}

fun Context.vertical(gravity: Int = Gravity.TOP): LinearLayout = LinearLayout(this).apply {
    orientation = LinearLayout.VERTICAL
    this.gravity = gravity
}

fun Context.horizontal(gravity: Int = Gravity.CENTER_VERTICAL): LinearLayout = LinearLayout(this).apply {
    orientation = LinearLayout.HORIZONTAL
    this.gravity = gravity
}

fun View.margins(top: Int = 0, bottom: Int = 0, left: Int = 0, right: Int = 0) {
    val lp = layoutParams ?: return
    if (lp is LinearLayout.LayoutParams) {
        lp.setMargins(dp(left), dp(top), dp(right), dp(bottom))
        layoutParams = lp
    }
}

private fun View.dp(value: Number): Int = (value.toFloat() * resources.displayMetrics.density).toInt()

class BrandMarkView(context: Context) : View(context) {
    private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val barPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFF0F7FF.toInt() }
    private val accentPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Palette.MINT }
    private val rect = RectF()

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        setMeasuredDimension(dp(46), dp(46))
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        rect.set(0f, 0f, w, h)
        bgPaint.shader = LinearGradient(0f, 0f, w, h,
            intArrayOf(0xFF1C3B40.toInt(), 0xFF214A5D.toInt(), 0xFF3D5D9C.toInt()), null, Shader.TileMode.CLAMP)
        canvas.drawRoundRect(rect, dp(14).toFloat(), dp(14).toFloat(), bgPaint)
        bgPaint.shader = null

        val barWidth = dp(3.2f).toFloat()
        val gap = dp(2.2f).toFloat()
        val heights = intArrayOf(dp(10), dp(20), dp(29), dp(17), dp(8))
        val total = heights.size * barWidth + (heights.size - 1) * gap
        var x = (w - total) / 2f
        val centerY = h / 2f - dp(3)
        for (height in heights) {
            rect.set(x, centerY - height / 2f, x + barWidth, centerY + height / 2f)
            canvas.drawRoundRect(rect, barWidth / 2f, barWidth / 2f, barPaint)
            x += barWidth + gap
        }
        rect.set(w / 2f - dp(10), h - dp(11), w / 2f + dp(10), h - dp(8))
        canvas.drawRoundRect(rect, dp(2).toFloat(), dp(2).toFloat(), accentPaint)
    }
}

class WaveOrbView(context: Context) : View(context) {
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(1).toFloat()
        color = 0xFF2C4258.toInt()
    }
    private val glow = Paint(Paint.ANTI_ALIAS_FLAG)
    private val barPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Palette.MINT }
    private val rect = RectF()

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = resolveSize(dp(210), widthMeasureSpec)
        setMeasuredDimension(size, size)
    }

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        val cx = w / 2f
        val cy = h / 2f
        stroke.alpha = 190
        canvas.drawCircle(cx, cy, w * 0.48f, stroke)
        stroke.alpha = 130
        canvas.drawCircle(cx, cy, w * 0.36f, stroke)

        val radius = w * 0.27f
        glow.shader = RadialGradient(cx, cy - w * 0.03f, radius,
            intArrayOf(0xFF56D8BC.toInt(), 0xFF1E4A45.toInt(), 0xFF10212A.toInt()),
            floatArrayOf(0f, 0.55f, 1f), Shader.TileMode.CLAMP)
        canvas.drawCircle(cx, cy, radius, glow)
        glow.shader = null

        val heights = intArrayOf(dp(18), dp(34), dp(52), dp(28), dp(15))
        val barWidth = dp(4).toFloat()
        val gap = dp(4).toFloat()
        val total = heights.size * barWidth + (heights.size - 1) * gap
        var x = cx - total / 2f
        for (height in heights) {
            rect.set(x, cy - height / 2f, x + barWidth, cy + height / 2f)
            canvas.drawRoundRect(rect, barWidth / 2f, barWidth / 2f, barPaint)
            x += barWidth + gap
        }
    }
}
