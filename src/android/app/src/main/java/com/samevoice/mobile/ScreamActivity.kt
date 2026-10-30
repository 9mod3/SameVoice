package com.samevoice.mobile

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * Dedicated Scream receiver screen. It is intentionally separate from the
 * SameVoice host/source mode: Scream is a Windows kernel virtual sound card and
 * sends its own PCM multicast stream, so this mode does not use the SameVoice
 * control protocol.
 */
class ScreamActivity : Activity() {
    private lateinit var statusBadge: TextView
    private lateinit var statusText: TextView
    private lateinit var formatText: TextView
    private lateinit var packetsText: TextView
    private lateinit var levelText: TextView
    private lateinit var errorText: TextView
    private lateinit var startButton: TextView
    private val handler = Handler(Looper.getMainLooper())

    private val poll = object : Runnable {
        override fun run() {
            refreshUi()
            handler.postDelayed(this, 500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildUi())
        handler.post(poll)
    }

    override fun onDestroy() {
        handler.removeCallbacks(poll)
        super.onDestroy()
    }

    private fun buildUi(): View {
        val root = vertical().apply {
            setPadding(dp(20), dp(26), dp(20), dp(30))
            setBackgroundColor(Palette.BG)
        }
        val header = horizontal().apply { gravity = Gravity.CENTER_VERTICAL }
        val back = iconButton("‹").apply {
            setOnClickListener { finish() }
        }
        header.addView(back)
        val titleBox = vertical().apply {
            setPadding(dp(12), 0, 0, 0)
        }
        titleBox.addView(text("Scream 接收", 24f, Palette.TEXT, bold = true))
        titleBox.addView(text("接收 Windows Scream 虚拟声卡的局域网 PCM", 10.5f, Palette.SECONDARY).apply {
            margins(top = 3)
        })
        header.addView(titleBox, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(header)

        val statusCard = card(padding = 18).apply { margins(top = 18) }
        val badgeRow = horizontal().apply { gravity = Gravity.CENTER_VERTICAL }
        statusBadge = pill("未启动", Palette.SECONDARY, 0xFF20293B.toInt()).apply {
            setPadding(dp(10), dp(5), dp(10), dp(5))
        }
        badgeRow.addView(statusBadge)
        statusText = text("Scream 接收未启动", 12f, Palette.TEXT, medium = true).apply {
            setPadding(dp(9), 0, 0, 0)
        }
        badgeRow.addView(statusText)
        statusCard.addView(badgeRow)
        formatText = text("格式：—", 10.5f, Palette.SECONDARY).apply { margins(top = 12) }
        statusCard.addView(formatText)
        packetsText = text("数据包：0 · 0 KB", 10.5f, Palette.SECONDARY).apply { margins(top = 5) }
        statusCard.addView(packetsText)
        levelText = text("电平：0%", 10.5f, Palette.SECONDARY).apply { margins(top = 5) }
        statusCard.addView(levelText)
        errorText = text("", 10.5f, Palette.DANGER).apply {
            visibility = View.GONE
            margins(top = 9)
        }
        statusCard.addView(errorText)
        startButton = primaryButton("开始接收").apply {
            margins(top = 16)
            setOnClickListener { toggleReceiver() }
        }
        statusCard.addView(startButton)
        root.addView(statusCard)

        val infoCard = card(padding = 18).apply { margins(top = 16) }
        infoCard.addView(text("Windows 端准备", 13f, Palette.TEXT, medium = true))
        infoCard.addView(text(
            "1. 安装 Scream 虚拟声卡驱动（需要管理员权限；Windows 11 可能需要在测试模式下安装）。\n" +
                "2. 在 Windows 声音设置中把输出设备设为 Scream；或把需要共享的播放器输出设为 Scream。\n" +
                "3. Scream 的 Advanced 设置建议使用 48 kHz / 16-bit / 立体声。\n" +
                "4. Windows 防火墙放行 UDP 4010；手机与电脑在同一 Wi-Fi，并关闭路由器的 AP 隔离。\n" +
                "5. Scream 默认组播到 239.255.77.77:4010；如果组播不可用，可在 Scream 注册表中改为单播到本机 IP:4010。\n" +
                "6. 此模式不经过同声的 SameVoice 协议，因此只接收 Scream 单路音频。",
            10.5f, Palette.SECONDARY
        ).apply {
            setLineSpacing(0f, 1.55f)
            margins(top = 9)
        })
        root.addView(infoCard)

        val tip = text(
            "提示：Scream 驱动由第三方提供，本应用不包含驱动程序。安装包和签名说明见 docs/SCREAM.md。",
            9.5f, Palette.MUTED
        ).apply {
            setLineSpacing(0f, 1.4f)
            margins(top = 14)
        }
        root.addView(tip)

        return ScrollView(this).apply {
            setBackgroundColor(Palette.BG)
            isFillViewport = true
            fitsSystemWindows = true
            addView(root)
        }
    }

    private fun toggleReceiver() {
        if (ScreamHub.running || ScreamHub.starting) {
            startService(Intent(this, ScreamService::class.java).setAction(ScreamService.ACTION_STOP))
        } else {
            val intent = Intent(this, ScreamService::class.java).setAction(ScreamService.ACTION_START)
            startForegroundService(intent)
        }
    }

    private fun refreshUi() {
        val running = ScreamHub.running
        val starting = ScreamHub.starting
        val error = ScreamHub.error
        when {
            error != null -> {
                setBadge("错误", Palette.DANGER, 0xFF331C25.toInt())
                statusText.text = "Scream 接收失败"
                errorText.text = error
                errorText.visibility = View.VISIBLE
            }
            starting -> {
                setBadge("启动中", Palette.WARN, 0xFF2E2618.toInt())
                statusText.text = "正在加入 Scream 组播…"
                errorText.visibility = View.GONE
            }
            running -> {
                setBadge("接收中", Palette.MINT, 0xFF1D2A44.toInt())
                statusText.text = ScreamHub.status
                errorText.visibility = View.GONE
            }
            else -> {
                setBadge("未启动", Palette.SECONDARY, 0xFF20293B.toInt())
                statusText.text = "Scream 接收未启动"
                errorText.visibility = View.GONE
            }
        }
        formatText.text = "格式：${ScreamHub.format.ifBlank { "—" }}"
        packetsText.text = "数据包：${ScreamHub.packets} · ${ScreamHub.bytes / 1024} KB"
        levelText.text = "电平：${(ScreamHub.level * 100).toInt()}%"
        startButton.text = if (running || starting) "停止接收" else "开始接收"
        startButton.background = if (running || starting) {
            ripple(roundedBackground(0xFF2A1A22.toInt(), 14, 0xFF5A3544.toInt()))
        } else {
            ripple(gradientBackground(intArrayOf(0xFF7DF3CD.toInt(), 0xFF91B8FF.toInt()), radiusDp = 14))
        }
        startButton.setTextColor(if (running || starting) 0xFFFFD9DE.toInt() else 0xFF07120F.toInt())
    }

    private fun setBadge(label: String, dotColor: Int, background: Int) {
        statusBadge.text = "●  $label"
        statusBadge.setTextColor(dotColor)
        statusBadge.background = roundedBackground(background, 99)
    }
}
