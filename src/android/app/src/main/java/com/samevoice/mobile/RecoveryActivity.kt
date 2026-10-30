package com.samevoice.mobile

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.os.Bundle
import android.text.method.ScrollingMovementMethod
import android.view.Gravity
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/**
 * Minimal, dependency-free recovery screen. It is shown after a main-thread
 * crash so the user can copy the stack trace and try again.
 */
class RecoveryActivity : Activity() {
    companion object {
        const val EXTRA_LOG = "log"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val log = intent.getStringExtra(EXTRA_LOG) ?: CrashStore.read(this) ?: "没有找到崩溃日志。"

        val density = resources.displayMetrics.density
        fun dip(value: Int) = (value * density).toInt()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dip(22), dip(32), dip(22), dip(28))
            setBackgroundColor(0xFF080B13.toInt())
        }
        root.addView(TextView(this).apply {
            text = "同声上次启动失败"
            setTextColor(0xFFF4F7FF.toInt())
            textSize = 23f
            typeface = Typeface.create("sans-serif", Typeface.BOLD)
            includeFontPadding = false
        })
        root.addView(TextView(this).apply {
            text = "错误已记录在本机。请复制下面的日志发给开发者；也可以清除日志后重试。"
            setTextColor(0xFF8C96AC.toInt())
            textSize = 12f
            setLineSpacing(0f, 1.35f)
            setPadding(0, dip(12), 0, 0)
        })
        root.addView(TextView(this).apply {
            text = log
            setTextColor(0xFFD7E0F0.toInt())
            textSize = 10f
            typeface = Typeface.MONOSPACE
            setBackgroundColor(0xFF111624.toInt())
            setPadding(dip(12), dip(12), dip(12), dip(12))
            movementMethod = ScrollingMovementMethod()
            setTextIsSelectable(true)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dip(390)
            ).apply { topMargin = dip(16) }
        })

        val copy = Button(this).apply {
            text = "复制错误日志"
            setOnClickListener {
                try {
                    val clipboard = getSystemService(ClipboardManager::class.java)
                    clipboard.setPrimaryClip(ClipData.newPlainText("SameVoice 错误日志", log))
                    Toast.makeText(this@RecoveryActivity, "已复制", Toast.LENGTH_SHORT).show()
                } catch (t: Throwable) {
                    Toast.makeText(this@RecoveryActivity, "复制失败：${t.message}", Toast.LENGTH_SHORT).show()
                }
            }
        }
        val retry = Button(this).apply {
            text = "清除日志并重试"
            setOnClickListener {
                CrashStore.clear(this@RecoveryActivity)
                startActivity(
                    Intent(this@RecoveryActivity, MainActivity::class.java).apply {
                        addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_NEW_TASK)
                        putExtra(MainActivity.EXTRA_SKIP_RECOVERY, true)
                    }
                )
                finish()
            }
        }
        val close = Button(this).apply {
            text = "退出"
            setOnClickListener { finishAffinity() }
        }
        val buttons = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dip(16), 0, 0)
            addView(copy, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ))
            addView(retry, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = dip(8) })
            addView(close, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = dip(8) })
        }
        root.addView(buttons)

        val scroll = ScrollView(this).apply {
            setBackgroundColor(0xFF080B13.toInt())
            isFillViewport = true
            addView(root, ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ))
        }
        setContentView(scroll)
    }
}
