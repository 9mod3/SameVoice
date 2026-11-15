package com.samevoice.mobile

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.projection.MediaProjectionManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.InputType
import android.util.Patterns
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import kotlin.math.roundToInt
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.Executors

class MainActivity : Activity() {
    companion object {
        const val EXTRA_SKIP_RECOVERY = "skip_recovery"
        private const val REQ_RECORD_AUDIO = 1001
        private const val REQ_PROJECTION = 1002
    }

    private lateinit var prefs: SharedPreferences
    private lateinit var deviceName: String
    private var selectedMode = HostMode.BALANCED
    private var hostBufferMs = 240
    private var masterVolume = 0.8f
    private var hostQuality = AudioQuality.HIGH
    private var hosts: List<HostOffer> = emptyList()
    private var scanning = false
    private var pendingHost: HostOffer? = null
    private var roleHost = true
    private var lastShareErrorShown: String? = null
    private var lastHostErrorShown: String? = null
    private val executor = Executors.newSingleThreadExecutor()
    private val handler = Handler(Looper.getMainLooper())

    private lateinit var content: LinearLayout
    private lateinit var hostPage: LinearLayout
    private lateinit var sharePage: LinearLayout
    private lateinit var hostTab: TextView
    private lateinit var shareTab: TextView
    private lateinit var headerSubtitle: TextView

    private lateinit var hostBadgeContainer: LinearLayout
    private lateinit var hostBadgeDot: TextView
    private lateinit var hostBadgeText: TextView
    private lateinit var hostStartButton: TextView
    private lateinit var hostDescription: TextView
    private lateinit var hostIpValue: TextView
    private lateinit var hostPortValue: TextView
    private lateinit var hostSourceValue: TextView
    private lateinit var hostVolumeValue: TextView
    private lateinit var hostModePill: TextView
    private lateinit var hostStatusDetail: TextView
    private lateinit var hostErrorText: TextView
    private lateinit var hostSourcesCount: TextView
    private lateinit var hostSourcesContainer: LinearLayout
    private lateinit var hostSourcesEmpty: TextView
    private lateinit var syncOption: LinearLayout
    private lateinit var lowOption: LinearLayout
    private lateinit var syncDot: TextView
    private lateinit var lowDot: TextView
    private lateinit var bufferSeek: SeekBar
    private lateinit var bufferValue: TextView
    private lateinit var qualityStandardPill: TextView
    private lateinit var qualityHighPill: TextView

    private lateinit var shareBadgeContainer: LinearLayout
    private lateinit var shareBadgeDot: TextView
    private lateinit var shareBadgeText: TextView
    private lateinit var shareTitle: TextView
    private lateinit var shareDescription: TextView
    private lateinit var shareActionButton: TextView
    private lateinit var shareDetailsCard: LinearLayout
    private lateinit var shareHostValue: TextView
    private lateinit var shareRttValue: TextView
    private lateinit var sharePacketsValue: TextView
    private lateinit var shareHostsContainer: LinearLayout
    private lateinit var shareHostsEmpty: TextView
    private lateinit var shareHostsScanning: TextView
    private lateinit var shareHostCount: TextView
    private lateinit var manualIp: EditText
    private lateinit var shareErrorText: TextView

    private val sourceViews = HashMap<Int, SourceRow>()
    private var lastSourceSignature = ""

    private data class SourceRow(
        val card: View,
        val statusDot: TextView,
        val statusText: TextView,
        val bufferText: TextView,
        val volumeBar: SeekBar,
        val volumeValue: TextView
    )

    private val pollRunnable = object : Runnable {
        override fun run() {
            try {
                refreshAll()
                handler.postDelayed(this, 550)
            } catch (t: Throwable) {
                reportUiCrash(t)
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (!intent.getBooleanExtra(EXTRA_SKIP_RECOVERY, false)) {
            val previousCrash = CrashStore.read(this)
            if (previousCrash != null && CrashStore.isMainThreadCrash(previousCrash)) {
                try {
                    startActivity(Intent(this, RecoveryActivity::class.java).apply {
                        putExtra(RecoveryActivity.EXTRA_LOG, previousCrash)
                    })
                } catch (_: Throwable) {
                }
                finish()
                return
            }
        }

        try {
            prefs = getSharedPreferences("samevoice", MODE_PRIVATE)
            deviceName = prefs.getString("deviceName", null)?.takeIf { it.isNotBlank() }
                ?: NetUtil.defaultDeviceName()
            selectedMode = HostMode.fromWire(prefs.getString("mode", null))
            hostBufferMs = prefs.getInt("bufferMs", 240).coerceIn(40, 1000)
            masterVolume = prefs.getFloat("volume", 0.8f).coerceIn(0f, 2f)
            hostQuality = AudioQuality.fromBits(prefs.getInt("hostQualityBits", 24))

            setContentView(buildUi())
            updateRoleTabs()
            updateModeSelection()
            refreshAll()
        } catch (t: Throwable) {
            reportUiCrash(t)
        }
    }

    private fun reportUiCrash(t: Throwable) {
        CrashStore.save(this, Thread.currentThread(), t)
        try {
            handler.removeCallbacksAndMessages(null)
            startActivity(Intent(this, RecoveryActivity::class.java).apply {
                putExtra(RecoveryActivity.EXTRA_LOG, CrashStore.read(this@MainActivity))
            })
        } catch (_: Throwable) {
        }
        finish()
    }

    override fun onResume() {
        super.onResume()
        handler.removeCallbacks(pollRunnable)
        handler.post(pollRunnable)
    }

    override fun onPause() {
        handler.removeCallbacks(pollRunnable)
        super.onPause()
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        executor.shutdownNow()
        super.onDestroy()
    }

    // ---------------------------------------------------------------- UI build

    private fun buildUi(): View {
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            overScrollMode = View.OVER_SCROLL_NEVER
            setBackgroundColor(Palette.BG)
        }
        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(16), dp(18), dp(30))
            fitsSystemWindows = true
        }
        scroll.addView(content, ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)

        content.addView(buildHeader())
        content.addView(buildTabs())
        content.addView(space(18))

        hostPage = buildHostPage()
        sharePage = buildSharePage().apply { visibility = View.GONE }
        content.addView(hostPage)
        content.addView(sharePage)
        content.addView(buildFooter())

        return scroll
    }

    private fun buildHeader(): LinearLayout {
        val header = horizontal()
        val mark = BrandMarkView(this)
        header.addView(mark, LinearLayout.LayoutParams(dp(46), dp(46)))
        val titleBox = vertical()
        val titleRow = horizontal()
        titleRow.addView(text("同声", 21f, Palette.TEXT, bold = true))
        titleRow.addView(text("  SAMEVOICE", 8.5f, Palette.MUTED, medium = true).apply {
            letterSpacing = 0.14f
            gravity = Gravity.BOTTOM
            setPadding(0, 0, 0, dp(3))
        })
        titleBox.addView(titleRow)
        headerSubtitle = text("局域网多设备音频汇聚", 10.5f, Palette.MUTED)
        headerSubtitle.margins(top = 4)
        titleBox.addView(headerSubtitle)
        val titleParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        titleParams.marginStart = dp(12)
        header.addView(titleBox, titleParams)

        val settings = iconButton("⚙")
        settings.contentDescription = "设置"
        settings.setOnClickListener { showSettingsDialog() }
        header.addView(settings)
        return header
    }

    private fun buildTabs(): LinearLayout {
        val tabs = horizontal().apply {
            background = roundedBackground(0xFF0D1320.toInt(), 16, 0xFF1D2637.toInt())
            setPadding(dp(4), dp(4), dp(4), dp(4))
        }
        tabs.margins(top = 20)
        hostTab = text("输出中心", 13f, Palette.TEXT, medium = true).apply {
            gravity = Gravity.CENTER
            isClickable = true
            setPadding(0, dp(11), 0, dp(11))
            setOnClickListener { switchRole(true) }
        }
        shareTab = text("共享声音", 13f, Palette.SECONDARY, medium = true).apply {
            gravity = Gravity.CENTER
            isClickable = true
            setPadding(0, dp(11), 0, dp(11))
            setOnClickListener { switchRole(false) }
        }
        tabs.addView(hostTab, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        tabs.addView(shareTab, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        return tabs
    }

    private fun buildHostPage(): LinearLayout {
        val page = vertical()

        val hero = card(heroBackground(), 20)
        val top = horizontal(Gravity.TOP)
        val left = vertical()
        val badgeRow = horizontal()
        hostBadgeContainer = horizontal().apply {
            background = roundedBackground(0xFF20342F.toInt(), 99)
            setPadding(dp(9), dp(5), dp(9), dp(5))
        }
        hostBadgeDot = text("●", 8.5f, Palette.MUTED)
        hostBadgeText = text("离线", 10f, 0xFFB8EBDD.toInt(), medium = true)
        hostBadgeContainer.addView(hostBadgeDot)
        val badgeTextParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(6) }
        hostBadgeContainer.addView(hostBadgeText, badgeTextParams)
        badgeRow.addView(hostBadgeContainer)
        badgeRow.addView(text("输出端", 10f, Palette.MUTED).apply {
            margins(left = 9)
        })
        left.addView(badgeRow)
        left.addView(text("让所有声音，\n在这里汇合。", 25f, Palette.TEXT, bold = true).apply {
            setLineSpacing(0f, 1.16f)
            margins(top = 14)
        })
        val topParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        top.addView(left, topParams)
        val orb = WaveOrbView(this)
        val orbParams = LinearLayout.LayoutParams(dp(100), dp(100)).apply { marginStart = dp(4) }
        top.addView(orb, orbParams)
        hero.addView(top)

        hostDescription = text(
            "接收同一 Wi-Fi 下多台设备分享的声音，按采集时间对齐后，从这台手机的扬声器同步混音播放。",
            12f, Palette.SECONDARY
        ).apply {
            margins(top = 14)
            setLineSpacing(0f, 1.35f)
        }
        hero.addView(hostDescription)

        val actionRow = horizontal()
        hostStartButton = primaryButton("启动输出")
        hostStartButton.setOnClickListener {
            if (ServiceHub.hostRunning || ServiceHub.hostStarting) stopHost() else startHost()
        }
        actionRow.addView(hostStartButton, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val copy = secondaryButton("复制地址")
        copy.setOnClickListener { copyLocalAddress() }
        val copyParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(10) }
        actionRow.addView(copy, copyParams)
        actionRow.margins(top = 20)
        hero.addView(actionRow)

        hero.addView(text(
            "关闭应用或息屏后仍会在前台服务中继续播放，不抢占其他应用的音频焦点。",
            10f, Palette.MUTED
        ).apply {
            margins(top = 13)
            setLineSpacing(0f, 1.3f)
        })
        page.addView(hero)

        // Mode card
        val modeCard = card(padding = 18)
        modeCard.margins(top = 14)
        val modeHeader = horizontal()
        modeHeader.addView(text("播放策略", 14f, Palette.TEXT, medium = true))
        hostModePill = pill("同步优先", Palette.MINT, 0xFF15231F.toInt())
        modeHeader.addView(hostModePill, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(8) })
        modeCard.addView(modeHeader)
        modeCard.addView(text("多路音源使用同一输出时钟对齐；模式可以随时切换。", 10.5f, Palette.MUTED).apply {
            margins(top = 6)
        })

        syncOption = buildModeOption(HostMode.BALANCED, "同步优先", "约 240 ms 缓冲，多设备严格对齐", true)
        lowOption = buildModeOption(HostMode.LOW, "最低延迟", "约 120 ms 缓冲，优先速度，适合稳定 Wi-Fi", false)
        modeCard.addView(syncOption)
        modeCard.addView(lowOption)
        modeCard.addView(text("切换策略会重建缓冲，约 0.2 秒静音属于正常现象。", 9.5f, Palette.MUTED).apply {
            margins(top = 9)
        })
        page.addView(modeCard)

        // Manual buffer card
        val bufferCard = card(padding = 18)
        bufferCard.margins(top = 14)
        val bufferHeader = horizontal()
        bufferHeader.addView(text("手动缓冲延迟", 14f, Palette.TEXT, medium = true))
        bufferValue = text("$hostBufferMs ms", 11f, Palette.MINT, medium = true).apply {
            gravity = Gravity.END
        }
        bufferHeader.addView(bufferValue, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        bufferCard.addView(bufferHeader)
        bufferCard.addView(text(
            "40–1000 ms。越低延迟越小，但 Wi-Fi 抖动时更容易断音；调整后会清空缓冲，短暂静音。",
            10f, Palette.MUTED
        ).apply {
            margins(top = 6)
            setLineSpacing(0f, 1.3f)
        })
        bufferSeek = SeekBar(this).apply {
            min = 40
            max = 1000
            progress = hostBufferMs
            progressTintList = android.content.res.ColorStateList.valueOf(Palette.MINT)
            thumbTintList = android.content.res.ColorStateList.valueOf(Palette.MINT)
            margins(top = 10)
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(seekBar: SeekBar?, progress: Int, fromUser: Boolean) {
                    if (!fromUser) return
                    hostBufferMs = progress
                    bufferValue.text = "$progress ms"
                    prefs.edit().putInt("bufferMs", progress).apply()
                    try { ServiceHub.host?.setBufferMs(progress) } catch (_: Exception) {}
                }
                override fun onStartTrackingTouch(seekBar: SeekBar?) {}
                override fun onStopTrackingTouch(seekBar: SeekBar?) {}
            })
        }
        bufferCard.addView(bufferSeek)
        page.addView(bufferCard)

        // Quality card
        val qualityCard = card(padding = 18)
        qualityCard.margins(top = 14)
        qualityCard.addView(text("传输音质", 14f, Palette.TEXT, medium = true))
        qualityCard.addView(text(
            "高音质使用 48 kHz / 24-bit PCM；音源和输出端都选高音质时生效，否则自动回退标准。",
            10f, Palette.MUTED
        ).apply {
            margins(top = 6)
            setLineSpacing(0f, 1.3f)
        })
        val qualityRow = horizontal().apply { margins(top = 11) }
        qualityStandardPill = pill("标准 16-bit", Palette.MUTED, 0xFF20293B.toInt()).apply {
            setPadding(dp(12), dp(7), dp(12), dp(7))
            setOnClickListener { selectHostQuality(AudioQuality.STANDARD) }
        }
        qualityHighPill = pill("高音质 24-bit", Palette.MUTED, 0xFF20293B.toInt()).apply {
            setPadding(dp(12), dp(7), dp(12), dp(7))
            setOnClickListener { selectHostQuality(AudioQuality.HIGH) }
        }
        qualityRow.addView(qualityStandardPill)
        qualityRow.addView(qualityHighPill, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(8) })
        qualityCard.addView(qualityRow)
        qualityCard.addView(text("切换音质需要重启输出端。", 9.5f, Palette.MUTED).apply {
            margins(top = 9)
        })
        page.addView(qualityCard)
        updateQualitySelection()

        // Status card
        val statusCard = card(padding = 18)
        statusCard.margins(top = 14)
        val statusTitle = horizontal()
        statusTitle.addView(text("运行状态", 14f, Palette.TEXT, medium = true))
        hostStatusDetail = text("等待启动", 10f, Palette.MUTED).apply { gravity = Gravity.END }
        statusTitle.addView(hostStatusDetail, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        statusCard.addView(statusTitle)

        hostIpValue = addInfoRow(statusCard, "本机地址")
        hostPortValue = addInfoRow(statusCard, "监听端口")
        hostSourceValue = addInfoRow(statusCard, "当前音源")

        val volumeRow = horizontal()
        volumeRow.margins(top = 2)
        volumeRow.addView(text("主音量", 11f, Palette.SECONDARY), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val minus = iconButton("−")
        minus.setOnClickListener { changeVolume(-5) }
        val plus = iconButton("+")
        plus.setOnClickListener { changeVolume(+5) }
        hostVolumeValue = text("${(masterVolume * 100).toInt()}%", 11.5f, Palette.TEXT, medium = true).apply {
            gravity = Gravity.CENTER
            width = dp(52)
        }
        volumeRow.addView(minus)
        volumeRow.addView(hostVolumeValue)
        volumeRow.addView(plus)
        val maxButton = pill("全满", Palette.MINT, 0xFF1D2A44.toInt()).apply {
            setPadding(dp(9), dp(5), dp(9), dp(5))
            setOnClickListener {
                changeVolume(200 - (masterVolume * 100).toInt())
                val host = ServiceHub.host
                if (host != null) {
                    try {
                        host.snapshot().sources.forEach { source ->
                            host.setSourceVolume(source.id, 1f)
                        }
                    } catch (_: Exception) {}
                }
                toast("输出端已设为最大")
            }
        }
        volumeRow.addView(maxButton, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(7) })
        statusCard.addView(volumeRow)

        hostErrorText = text("", 10.5f, Palette.DANGER).apply {
            visibility = View.GONE
            margins(top = 10)
        }
        statusCard.addView(hostErrorText)
        page.addView(statusCard)

        // Sources card
        val sourcesCard = card(padding = 18)
        sourcesCard.margins(top = 14)
        val sourcesHeader = horizontal()
        sourcesHeader.addView(text("正在混音", 14f, Palette.TEXT, medium = true))
        hostSourcesCount = pill("等待接入", Palette.SECONDARY, 0xFF19232F.toInt())
        sourcesHeader.addView(hostSourcesCount, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(8) })
        sourcesHeader.addView(text("自动软限幅", 9.5f, Palette.MUTED).apply { gravity = Gravity.END },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        sourcesCard.addView(sourcesHeader)
        hostSourcesEmpty = text(
            "还没有音源接入。\n在其他设备上打开同声，进入「共享声音」并选择这台手机。",
            11f, Palette.MUTED
        ).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(24), 0, dp(18))
            setLineSpacing(0f, 1.35f)
        }
        sourcesCard.addView(hostSourcesEmpty)
        hostSourcesContainer = vertical().apply { visibility = View.GONE }
        sourcesCard.addView(hostSourcesContainer)
        page.addView(sourcesCard)

        page.addView(text(
            "提示：输出端本身不采集自己的声音，避免形成回环；请让其他设备提供音源。",
            9.5f, Palette.MUTED
        ).apply {
            margins(top = 12)
            setLineSpacing(0f, 1.3f)
        })
        return page
    }

    private fun buildSharePage(): LinearLayout {
        val page = vertical()

        val hero = card(heroBackground(), 20)
        val badgeRow = horizontal()
        shareBadgeContainer = horizontal().apply {
            background = roundedBackground(0xFF1D2A44.toInt(), 99)
            setPadding(dp(9), dp(5), dp(9), dp(5))
        }
        shareBadgeDot = text("●", 8.5f, Palette.MUTED)
        shareBadgeText = text("待机", 10f, 0xFFC6D6F2.toInt(), medium = true)
        shareBadgeContainer.addView(shareBadgeDot)
        shareBadgeContainer.addView(shareBadgeText, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(6) })
        badgeRow.addView(shareBadgeContainer)
        badgeRow.addView(text("音源设备", 10f, Palette.MUTED).apply { margins(left = 9) })
        hero.addView(badgeRow)

        shareTitle = text("把本机声音，\n送到输出端。", 26f, Palette.TEXT, bold = true).apply {
            setLineSpacing(0f, 1.14f)
            margins(top = 14)
        }
        hero.addView(shareTitle)
        shareDescription = text(
            "先扫描并选择一台输出设备，再授权系统捕获本机正在播放的内部音频。",
            11.8f, Palette.SECONDARY
        ).apply {
            margins(top = 12)
            setLineSpacing(0f, 1.38f)
        }
        hero.addView(shareDescription)

        shareActionButton = primaryButton("扫描输出设备")
        shareActionButton.setOnClickListener {
            if (ServiceHub.source != null || ServiceHub.shareStarting) stopSharing() else scanHosts()
        }
        shareActionButton.margins(top = 18)
        hero.addView(shareActionButton)
        hero.addView(text(
            "需要系统录屏授权。Android 14 请选择「整个屏幕」，才能捕获其他应用的声音；受版权保护的内容可能被系统屏蔽。",
            9.8f, Palette.MUTED
        ).apply {
            margins(top = 12)
            setLineSpacing(0f, 1.32f)
        })
        shareErrorText = text("", 10.5f, Palette.DANGER).apply {
            visibility = View.GONE
            margins(top = 10)
        }
        hero.addView(shareErrorText)
        page.addView(hero)

        // Connection details
        shareDetailsCard = card(padding = 18).apply { visibility = View.GONE }
        shareDetailsCard.margins(top = 14)
        shareDetailsCard.addView(text("连接详情", 11f, Palette.MUTED))
        shareHostValue = text("尚未连接", 16f, Palette.TEXT, medium = true).apply { margins(top = 8) }
        shareDetailsCard.addView(shareHostValue)
        shareDetailsCard.addView(divider())
        val rttRow = horizontal()
        rttRow.addView(text("网络往返", 10.5f, Palette.MUTED), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        shareRttValue = text("—", 10.5f, Palette.TEXT)
        rttRow.addView(shareRttValue)
        shareDetailsCard.addView(rttRow)
        val packetRow = horizontal()
        packetRow.margins(top = 8)
        packetRow.addView(text("已发送", 10.5f, Palette.MUTED), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        sharePacketsValue = text("—", 10.5f, Palette.TEXT)
        packetRow.addView(sharePacketsValue)
        shareDetailsCard.addView(packetRow)
        page.addView(shareDetailsCard)

        // Nearby hosts
        val hostsCard = card(padding = 18)
        hostsCard.margins(top = 14)
        val hostsHeader = horizontal()
        val hostsTitleBox = vertical()
        hostsTitleBox.addView(text("附近的输出端", 14f, Palette.TEXT, medium = true))
        shareHostCount = text("未扫描", 10f, Palette.MUTED).apply { margins(top = 4) }
        hostsTitleBox.addView(shareHostCount)
        hostsHeader.addView(hostsTitleBox, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val scanButton = secondaryButton("扫描")
        scanButton.setOnClickListener { scanHosts() }
        hostsHeader.addView(scanButton)
        hostsCard.addView(hostsHeader)

        shareHostsScanning = text("正在扫描同一 Wi-Fi…", 10.5f, Palette.MUTED).apply {
            visibility = View.GONE
            gravity = Gravity.CENTER
            setPadding(0, dp(20), 0, dp(16))
        }
        hostsCard.addView(shareHostsScanning)
        shareHostsEmpty = text(
            "还没有发现输出端。\n请确认对方已启动「输出中心」，或使用下方手动连接。",
            10.5f, Palette.MUTED
        ).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(20), 0, dp(16))
            setLineSpacing(0f, 1.32f)
        }
        hostsCard.addView(shareHostsEmpty)
        shareHostsContainer = vertical().apply { visibility = View.GONE }
        hostsCard.addView(shareHostsContainer)
        page.addView(hostsCard)

        // Manual connect
        val manualCard = card(padding = 18)
        manualCard.margins(top = 14)
        manualCard.addView(text("手动连接", 14f, Palette.TEXT, medium = true))
        manualCard.addView(text("输入输出端显示的本机 IPv4 地址，可选带端口（默认 47820）。", 10f, Palette.MUTED).apply {
            margins(top = 5)
        })
        manualIp = EditText(this).apply {
            setText(prefs.getString("lastManualIp", "192.168.1.100"))
            hint = "192.168.1.100"
            textSize = 13f
            setTextColor(Palette.TEXT)
            setHintTextColor(Palette.MUTED)
            setSingleLine(true)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            includeFontPadding = false
            setPadding(dp(13), dp(11), dp(13), dp(11))
            background = roundedBackground(Palette.INPUT, 10, 0xFF263047.toInt())
            margins(top = 12)
        }
        manualCard.addView(manualIp)
        val manualButton = secondaryButton("连接这个地址")
        manualButton.setOnClickListener { connectManual() }
        manualButton.margins(top = 10)
        manualCard.addView(manualButton, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ))
        page.addView(manualCard)

        val infoCard = card(padding = 18)
        infoCard.margins(top = 14)
        infoCard.addView(text("共享须知", 12f, Palette.TEXT, medium = true))
        infoCard.addView(text(
            "• 一个输出端最多接入 8 台音源，每路约 1.54 Mbps 上行带宽。\n" +
                "• 请关闭路由器的 AP 隔离，并确保两台设备在同一个 IPv4 子网。\n" +
                "• Android 仅能捕获允许被捕获的内部音频；通话、麦克风和受 DRM 保护的声音不会被采集。\n" +
                "• 关闭应用后，前台服务会继续共享，直到你在通知中停止。",
            10f, Palette.MUTED
        ).apply {
            margins(top = 9)
            setLineSpacing(0f, 1.45f)
        })
        page.addView(infoCard)
        return page
    }

    private fun buildFooter(): LinearLayout {
        val footer = horizontal()
        footer.margins(top = 18)
        footer.addView(text("●", 7f, 0xFF4A566C.toInt()))
        footer.addView(text(
            " 局域网直连 · 不上传云端 · 不主动抢占音频焦点",
            9f, 0xFF4E5A6F.toInt()
        ), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        footer.addView(text("v1.0", 8.5f, 0xFF3E495C.toInt()))
        return footer
    }

    private fun buildModeOption(mode: HostMode, title: String, subtitle: String, sync: Boolean): LinearLayout {
        val option = horizontal()
        option.orientation = LinearLayout.HORIZONTAL
        option.setPadding(dp(13), dp(12), dp(13), dp(12))
        option.isClickable = true
        option.isFocusable = true
        val box = vertical()
        val titleRow = horizontal()
        titleRow.addView(text(title, 13f, Palette.TEXT, medium = true))
        if (sync) {
            val tag = pill("推荐", Palette.MINT, 0xFF213B33.toInt())
            titleRow.addView(tag, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { marginStart = dp(7) })
        }
        box.addView(titleRow)
        box.addView(text(subtitle, 10f, Palette.MUTED).apply { margins(top = 5) })
        option.addView(box, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val dot = text(if (sync) "●" else "○", 19f, if (sync) Palette.MINT else 0xFF7C8AA0.toInt())
        dot.gravity = Gravity.CENTER
        option.addView(dot, LinearLayout.LayoutParams(dp(28), dp(28)))
        option.setOnClickListener { selectMode(mode) }
        if (sync) {
            syncDot = dot
            option.margins(top = 12)
        } else {
            lowDot = dot
            option.margins(top = 8)
        }
        return option
    }

    private fun addInfoRow(parent: LinearLayout, label: String): TextView {
        val row = horizontal()
        row.margins(top = 9)
        row.addView(text(label, 11f, Palette.SECONDARY), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        val value = text("—", 11f, 0xFFD7E0F0.toInt(), medium = true).apply {
            gravity = Gravity.END
        }
        row.addView(value)
        parent.addView(row)
        return value
    }

    private fun heroBackground(): GradientDrawable = gradientBackground(
        intArrayOf(0xFF151D32.toInt(), 0xFF101725.toInt(), 0xFF0E1A1B.toInt()),
        GradientDrawable.Orientation.TL_BR,
        22,
        0xFF2A3550.toInt()
    )

    private fun space(height: Int): View = View(this).apply {
        layoutParams = LinearLayout.LayoutParams(1, dp(height))
    }

    // ---------------------------------------------------------------- role / mode

    private fun switchRole(host: Boolean) {
        if (roleHost == host) return
        roleHost = host
        hostPage.visibility = if (host) View.VISIBLE else View.GONE
        sharePage.visibility = if (host) View.GONE else View.VISIBLE
        headerSubtitle.text = if (host) "局域网多设备音频汇聚" else "把本机声音送到输出设备"
        updateRoleTabs()
        if (!host && hosts.isEmpty() && !scanning && ServiceHub.source == null) scanHosts()
        refreshAll()
    }

    private fun updateRoleTabs() {
        if (!::hostTab.isInitialized) return
        if (roleHost) {
            hostTab.background = roundedBackground(0xFF1A2931.toInt(), 13, 0xFF2E5149.toInt())
            hostTab.setTextColor(0xFFE8FFF8.toInt())
            shareTab.background = null
            shareTab.setTextColor(Palette.SECONDARY)
        } else {
            shareTab.background = roundedBackground(0xFF1A2438.toInt(), 13, 0xFF344465.toInt())
            shareTab.setTextColor(0xFFE8F0FF.toInt())
            hostTab.background = null
            hostTab.setTextColor(Palette.SECONDARY)
        }
    }

    private fun selectMode(mode: HostMode) {
        selectedMode = mode
        hostBufferMs = mode.bufferMs
        prefs.edit().putString("mode", mode.wire).putInt("bufferMs", hostBufferMs).apply()
        if (::bufferSeek.isInitialized) {
            bufferSeek.progress = hostBufferMs
            bufferValue.text = "$hostBufferMs ms"
        }
        updateModeSelection()
        if (ServiceHub.hostRunning) {
            try {
                startService(Intent(this, HostService::class.java).apply {
                    action = HostService.ACTION_SET_MODE
                    putExtra(HostService.EXTRA_MODE, mode.wire)
                })
            } catch (_: Exception) {}
        }
    }

    private fun updateModeSelection() {
        if (!::syncOption.isInitialized) return
        val selectedBg = 0xFF13211F.toInt()
        val selectedStroke = 0xFF3C7F6C.toInt()
        val idle = 0xFF0D121E.toInt()
        syncOption.background = roundedBackground(
            if (selectedMode == HostMode.BALANCED) selectedBg else idle, 15,
            if (selectedMode == HostMode.BALANCED) selectedStroke else 0xFF232C40.toInt()
        )
        lowOption.background = roundedBackground(
            if (selectedMode == HostMode.LOW) selectedBg else idle, 15,
            if (selectedMode == HostMode.LOW) selectedStroke else 0xFF232C40.toInt()
        )
        syncDot.text = if (selectedMode == HostMode.BALANCED) "●" else "○"
        syncDot.setTextColor(if (selectedMode == HostMode.BALANCED) Palette.MINT else 0xFF7C8AA0.toInt())
        lowDot.text = if (selectedMode == HostMode.LOW) "●" else "○"
        lowDot.setTextColor(if (selectedMode == HostMode.LOW) Palette.MINT else 0xFF7C8AA0.toInt())
        if (::hostModePill.isInitialized) hostModePill.text = selectedMode.label
    }

    private fun selectHostQuality(quality: AudioQuality) {
        if (hostQuality == quality) return
        hostQuality = quality
        prefs.edit().putInt("hostQualityBits", quality.bits).apply()
        updateQualitySelection()
        if (ServiceHub.hostRunning || ServiceHub.hostStarting) {
            toast("音质已保存，重启输出端后生效")
        }
    }

    private fun updateQualitySelection() {
        if (!::qualityStandardPill.isInitialized) return
        val selectedBg = 0xFF1D2A44.toInt()
        val idleBg = 0xFF20293B.toInt()
        qualityStandardPill.background = roundedBackground(
            if (hostQuality == AudioQuality.STANDARD) selectedBg else idleBg, 99
        )
        qualityStandardPill.setTextColor(
            if (hostQuality == AudioQuality.STANDARD) Palette.MINT else Palette.MUTED
        )
        qualityHighPill.background = roundedBackground(
            if (hostQuality == AudioQuality.HIGH) selectedBg else idleBg, 99
        )
        qualityHighPill.setTextColor(
            if (hostQuality == AudioQuality.HIGH) Palette.MINT else Palette.MUTED
        )
    }

    // ---------------------------------------------------------------- state refresh

    private fun refreshAll() {
        refreshHost()
        refreshShare()
        refreshHostsList()
    }

    private fun refreshHost() {
        val snapshot = ServiceHub.host?.snapshot()
        if (snapshot != null && snapshot.running) {
            setHostBadge("输出中", Palette.MINT, 0xFF20342F.toInt(), 0xFFB8EBDD.toInt())
            hostDescription.text = if (snapshot.sourceCount == 0) {
                "设备已经就绪。请在其他设备的「共享声音」中连接这台手机。"
            } else {
                "所有已开启共享的设备声音，正在这里同步混音并从默认扬声器播放。"
            }
            hostDescription.setTextColor(Palette.SECONDARY)
            setHostButton("停止输出", enabled = true)
            hostIpValue.text = snapshot.localAddresses
            hostPortValue.text = "TCP ${snapshot.controlPort} · UDP ${snapshot.audioPort}"
            hostSourceValue.text = if (snapshot.sourceCount == 1) "1 路音源" else "${snapshot.sourceCount} 路音源"
            hostStatusDetail.text = if (snapshot.sourceCount == 0) "等待接入" else "${snapshot.sourceCount} 路"
            if (snapshot.mode != selectedMode) {
                selectedMode = snapshot.mode
                updateModeSelection()
            }
            val volumePercent = (snapshot.masterVolume * 100).toInt()
            hostVolumeValue.text = "$volumePercent%"
            masterVolume = snapshot.masterVolume
            hostErrorText.visibility = View.GONE
            hostSourcesEmpty.text = "还没有音源接入。\n在其他设备上打开同声，进入「共享声音」并选择这台手机。"
            hostSourcesEmpty.visibility = if (snapshot.sourceCount == 0) View.VISIBLE else View.GONE
            hostSourcesContainer.visibility = if (snapshot.sourceCount > 0) View.VISIBLE else View.GONE
            hostSourcesCount.text = if (snapshot.sourceCount == 0) "等待接入" else "${snapshot.sourceCount} 路"
            updateSourceRows(snapshot.sources)
        } else if (ServiceHub.hostStarting) {
            setHostBadge("启动中", Palette.WARN, 0xFF2E2618.toInt(), 0xFFFFDCA8.toInt())
            hostDescription.text = "正在打开共享音频输出…"
            hostDescription.setTextColor(Palette.SECONDARY)
            setHostButton("正在启动…", enabled = false)
            hostStatusDetail.text = "启动中"
            hostErrorText.visibility = View.GONE
            hostSourcesEmpty.visibility = View.VISIBLE
            hostSourcesContainer.visibility = View.GONE
            hostSourcesEmpty.text = "音频输出正在初始化…"
        } else {
            setHostBadge("离线", Palette.MUTED, 0xFF1A2130.toInt(), Palette.SECONDARY)
            hostDescription.text = "启动后，同一 Wi-Fi 下的手机和其他电脑可以发现这台设备，把声音送到这里。"
            hostDescription.setTextColor(Palette.SECONDARY)
            setHostButton("启动输出", enabled = true)
            hostIpValue.text = NetUtil.localAddressText()
            hostPortValue.text = "TCP 47820 · UDP 47822"
            hostSourceValue.text = "0 路音源"
            hostStatusDetail.text = "等待启动"
            hostVolumeValue.text = "${(masterVolume * 100).toInt()}%"
            hostSourcesEmpty.visibility = View.VISIBLE
            hostSourcesContainer.visibility = View.GONE
            hostSourcesEmpty.text = "还没有音源接入。\n在其他设备上打开同声，进入「共享声音」并选择这台手机。"
            hostSourcesCount.text = "等待接入"
            val error = ServiceHub.hostError
            if (error != null) {
                hostErrorText.text = error
                hostErrorText.visibility = View.VISIBLE
                if (lastHostErrorShown != error) {
                    lastHostErrorShown = error
                    toast(error)
                }
            } else {
                hostErrorText.visibility = View.GONE
            }
        }
        updateHostButtonStyle()
    }

    private fun setHostButton(label: String, enabled: Boolean) {
        if (hostStartButton.text.toString() != label) hostStartButton.text = label
        hostStartButton.isEnabled = enabled
        hostStartButton.isClickable = enabled
    }

    private fun updateHostButtonStyle() {
        val running = ServiceHub.hostRunning || ServiceHub.hostStarting
        if (running) {
            hostStartButton.setTextColor(0xFFFFD9DE.toInt())
            hostStartButton.background = ripple(
                roundedBackground(0xFF2A1A22.toInt(), 14, 0xFF5A3544.toInt())
            )
            hostStartButton.alpha = if (ServiceHub.hostStarting) 0.55f else 1f
        } else {
            hostStartButton.setTextColor(0xFF07120F.toInt())
            hostStartButton.background = ripple(
                gradientBackground(intArrayOf(0xFF7DF3CD.toInt(), 0xFF91B8FF.toInt()), radiusDp = 14)
            )
            hostStartButton.alpha = 1f
        }
    }

    private fun setHostBadge(label: String, dotColor: Int, background: Int, textColor: Int) {
        hostBadgeText.text = label
        hostBadgeText.setTextColor(textColor)
        hostBadgeDot.setTextColor(dotColor)
        hostBadgeContainer.background = roundedBackground(background, 99)
    }

    private fun updateSourceRows(sources: List<HostSourceInfo>) {
        val signature = sources.joinToString("|") { "${it.id}:${it.name}:${it.sharing}" }
        if (signature != lastSourceSignature) {
            lastSourceSignature = signature
            hostSourcesContainer.removeAllViews()
            sourceViews.clear()
            for (source in sources) {
                val row = buildSourceRow(source)
                hostSourcesContainer.addView(row.card)
                sourceViews[source.id] = row
            }
        }
        for (source in sources) {
            val row = sourceViews[source.id] ?: continue
            val stale = source.sharing && source.lastPacketAgeMs > 3000
            row.statusDot.setTextColor(if (source.sharing) (if (stale) Palette.WARN else Palette.MINT) else Palette.MUTED)
            row.statusText.text = when {
                source.sharing && stale -> "信号中断"
                source.sharing -> "正在混音"
                else -> "已连接"
            }
            row.bufferText.text = if (source.sharing) "${source.bufferMs.toInt()} ms 缓冲" else "等待开始共享"
        }
    }

    private fun buildSourceRow(source: HostSourceInfo): SourceRow {
        val card = vertical().apply {
            background = roundedBackground(Palette.CARD_SOFT, 14, Palette.BORDER_SOFT)
            setPadding(dp(12), dp(11), dp(12), dp(10))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = dp(10) }
        }

        val top = horizontal()
        val initial = text(
            if (source.name.isBlank()) "?" else source.name.take(1).uppercase(),
            14f, Palette.MINT, bold = true
        ).apply {
            gravity = Gravity.CENTER
            background = roundedBackground(0xFF17242B.toInt(), 11)
        }
        top.addView(initial, LinearLayout.LayoutParams(dp(36), dp(36)))

        val textBox = vertical()
        textBox.addView(text(source.name, 12.5f, Palette.TEXT, medium = true))
        textBox.addView(text(
            "${if (source.platform == "android") "Android" else source.platform} · ${source.address}",
            9.5f, Palette.MUTED
        ).apply { margins(top = 4) })
        top.addView(textBox, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            marginStart = dp(10)
        })

        val statusBox = vertical()
        val statusLine = horizontal()
        val dot = text("●", 8f, Palette.MUTED)
        val statusText = text("已连接", 10f, 0xFFAAB7CB.toInt())
        statusLine.addView(dot)
        statusLine.addView(statusText, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(4) })
        statusBox.addView(statusLine)
        val bufferText = text("等待开始共享", 9f, Palette.MUTED).apply {
            gravity = Gravity.END
            margins(top = 4)
        }
        statusBox.addView(bufferText)
        top.addView(statusBox)
        card.addView(top)

        val initialVolume = (source.volume * 100).roundToInt().coerceIn(0, 100)
        val volumeRow = horizontal().apply {
            gravity = Gravity.CENTER_VERTICAL
            margins(top = 8)
        }
        volumeRow.addView(text("音量", 9.5f, Palette.MUTED))
        val volumeBar = SeekBar(this).apply {
            max = 100
            progress = initialVolume
            minHeight = dp(28)
            setPadding(0, 0, 0, 0)
            progressTintList = ColorStateList.valueOf(Palette.MINT)
            thumbTintList = ColorStateList.valueOf(Palette.MINT)
            progressBackgroundTintList = ColorStateList.valueOf(0xFF263146.toInt())
        }
        val volumeValue = text("$initialVolume%", 9.5f, Palette.SECONDARY).apply {
            gravity = Gravity.END
        }
        volumeRow.addView(volumeBar, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            marginStart = dp(8)
            marginEnd = dp(8)
        })
        volumeRow.addView(volumeValue, LinearLayout.LayoutParams(dp(36), LinearLayout.LayoutParams.WRAP_CONTENT))
        card.addView(volumeRow)

        volumeBar.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(bar: SeekBar?, progress: Int, fromUser: Boolean) {
                volumeValue.text = "$progress%"
                if (fromUser) {
                    ServiceHub.host?.setSourceVolume(source.id, progress / 100f)
                }
            }

            override fun onStartTrackingTouch(bar: SeekBar?) {}
            override fun onStopTrackingTouch(bar: SeekBar?) {}
        })

        return SourceRow(card, dot, statusText, bufferText, volumeBar, volumeValue)
    }

    private fun refreshShare() {
        val engine = ServiceHub.source
        val status = engine?.status()
        if (status != null) {
            when (status.state) {
                ShareState.CONNECTING -> {
                    setShareBadge("连接中", Palette.WARN, 0xFF2E2618.toInt(), 0xFFFFDCA8.toInt())
                    shareTitle.text = "正在连接输出端"
                    shareDescription.text = "正在和输出设备建立会话，并校准两端时钟…"
                    setShareButton("停止共享", true)
                }
                ShareState.SYNCING -> {
                    setShareBadge("校准中", Palette.WARN, 0xFF2E2618.toInt(), 0xFFFFDCA8.toInt())
                    shareTitle.text = "正在校准时钟"
                    shareDescription.text = "通过 NTP 式往返校准，让多台设备的声音精确对齐。"
                    setShareButton("停止共享", true)
                }
                ShareState.SHARING -> {
                    setShareBadge("共享中", Palette.MINT, 0xFF1D2A44.toInt(), 0xFFCFF9EC.toInt())
                    shareTitle.text = "正在共享本机声音"
                    shareDescription.text = "声音已送往 ${status.hostName}。关闭应用后，前台服务仍会继续共享。"
                    setShareButton("停止共享", true)
                }
                ShareState.ERROR -> {
                    setShareBadge("异常", Palette.DANGER, 0xFF331C25.toInt(), 0xFFFFD3DA.toInt())
                    shareTitle.text = "共享中断"
                    shareDescription.text = status.message
                    setShareButton("停止共享", true)
                }
                ShareState.IDLE -> {
                    setShareBadge("待机", Palette.MUTED, 0xFF1D2A44.toInt(), 0xFFC6D6F2.toInt())
                    shareTitle.text = "把本机声音，\n送到输出端。"
                    shareDescription.text = "先扫描并选择一台输出设备，再授权系统捕获本机正在播放的内部音频。"
                    setShareButton("扫描输出设备", true)
                }
            }
            val active = status.state != ShareState.IDLE
            shareDetailsCard.visibility = if (active) View.VISIBLE else View.GONE
            if (active) {
                shareHostValue.text = status.hostName.ifBlank { "输出端" }
                shareRttValue.text = if (status.rttMs >= 0) "${"%.0f".format(status.rttMs)} ms" else "同步中"
                sharePacketsValue.text = if (status.packetsSent > 0) {
                    "${status.packetsSent} 包 · ${"%.1f".format(status.bytesSent / 1024.0 / 1024.0)} MB"
                } else "—"
            }
            if (status.state == ShareState.ERROR) {
                shareErrorText.text = status.message
                shareErrorText.visibility = View.VISIBLE
            } else {
                shareErrorText.visibility = View.GONE
            }
            lastShareErrorShown = null
        } else if (ServiceHub.shareStarting) {
            setShareBadge("启动中", Palette.WARN, 0xFF2E2618.toInt(), 0xFFFFDCA8.toInt())
            shareTitle.text = "正在启动共享"
            shareDescription.text = "正在请求系统授权并打开内部音频采集…"
            setShareButton("取消", true)
            shareDetailsCard.visibility = View.GONE
            shareErrorText.visibility = View.GONE
        } else {
            setShareBadge("待机", Palette.MUTED, 0xFF1D2A44.toInt(), 0xFFC6D6F2.toInt())
            shareTitle.text = "把本机声音，\n送到输出端。"
            shareDescription.text = "先扫描并选择一台输出设备，再授权系统捕获本机正在播放的内部音频。"
            setShareButton("扫描输出设备", true)
            shareDetailsCard.visibility = View.GONE
            val error = ServiceHub.shareError
            if (error != null) {
                shareErrorText.text = error
                shareErrorText.visibility = View.VISIBLE
                if (lastShareErrorShown != error) {
                    lastShareErrorShown = error
                    toast(error)
                }
            } else {
                shareErrorText.visibility = View.GONE
            }
        }
        updateShareButtonStyle(engine != null || ServiceHub.shareStarting)
    }

    private fun setShareButton(label: String, enabled: Boolean) {
        if (shareActionButton.text.toString() != label) shareActionButton.text = label
        shareActionButton.isEnabled = enabled
        shareActionButton.isClickable = enabled
    }

    private fun updateShareButtonStyle(active: Boolean) {
        if (active) {
            shareActionButton.setTextColor(0xFFFFD9DE.toInt())
            shareActionButton.background = ripple(
                roundedBackground(0xFF2A1A22.toInt(), 14, 0xFF5A3544.toInt())
            )
        } else {
            shareActionButton.setTextColor(0xFF07120F.toInt())
            shareActionButton.background = ripple(
                gradientBackground(intArrayOf(0xFF7DF3CD.toInt(), 0xFF91B8FF.toInt()), radiusDp = 14)
            )
        }
    }

    private fun setShareBadge(label: String, dotColor: Int, background: Int, textColor: Int) {
        shareBadgeText.text = label
        shareBadgeText.setTextColor(textColor)
        shareBadgeDot.setTextColor(dotColor)
        shareBadgeContainer.background = roundedBackground(background, 99)
    }

    private fun refreshHostsList() {
        if (!::shareHostsContainer.isInitialized) return
        shareHostsScanning.visibility = if (scanning) View.VISIBLE else View.GONE
        shareHostsEmpty.visibility = if (!scanning && hosts.isEmpty()) View.VISIBLE else View.GONE
        shareHostsContainer.visibility = if (hosts.isNotEmpty()) View.VISIBLE else View.GONE
        shareHostCount.text = when {
            scanning -> "正在扫描"
            hosts.isEmpty() -> "未发现输出端"
            else -> "发现 ${hosts.size} 台设备"
        }
        if (shareHostsContainer.tag != hosts) {
            shareHostsContainer.tag = hosts
            shareHostsContainer.removeAllViews()
            for (offer in hosts) {
                shareHostsContainer.addView(buildHostRow(offer))
            }
        }
    }

    private fun buildHostRow(offer: HostOffer): LinearLayout {
        val row = horizontal().apply {
            background = roundedBackground(Palette.CARD_SOFT, 16, Palette.BORDER_SOFT)
            setPadding(dp(13), dp(12), dp(13), dp(12))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = dp(10) }
        }
        val initial = text(offer.name.take(1).uppercase(), 14f, Palette.VIOLET, bold = true).apply {
            gravity = Gravity.CENTER
            background = roundedBackground(0xFF1B2740.toInt(), 10)
        }
        row.addView(initial, LinearLayout.LayoutParams(dp(34), dp(34)))
        val box = vertical()
        val titleRow = horizontal()
        titleRow.addView(text(offer.name, 12.5f, Palette.TEXT, medium = true))
        titleRow.addView(pill(offer.mode.label, 0xFFAABDF5.toInt(), 0xFF1D2A44.toInt()).apply {
            setPadding(dp(6), dp(2), dp(6), dp(2))
            textSize = 8.5f
        }, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = dp(7) })
        box.addView(titleRow)
        box.addView(text(
            "${offer.address}:${offer.tcpPort} · ${if (offer.sources == 0) "尚无声源" else "${offer.sources} 路音源"}",
            9.5f, Palette.MUTED
        ).apply { margins(top = 4) })
        row.addView(box, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            marginStart = dp(10)
        })
        val connect = secondaryButton("连接")
        connect.textSize = 11f
        connect.setPadding(dp(14), dp(8), dp(14), dp(8))
        connect.minHeight = dp(36)
        connect.setOnClickListener { beginConnect(offer) }
        row.addView(connect)
        return row
    }

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }

    private fun copyLocalAddress() {
        val address = NetUtil.localIpv4().firstOrNull() ?: run {
            toast("没有找到局域网 IPv4 地址")
            return
        }
        val clipboard = getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText("SameVoice 地址", address))
        toast("已复制 $address")
    }

    // ---------------------------------------------------------------- actions

    private fun startHost() {
        if (ServiceHub.source != null || ServiceHub.shareStarting) {
            toast("请先停止共享本机声音")
            return
        }
        if (ServiceHub.hostRunning || ServiceHub.hostStarting) return
        ServiceHub.clearHostError()
        ServiceHub.hostStarting = true
        hostStartedLocally = true
        prefs.edit()
            .putString("deviceName", deviceName)
            .putString("mode", selectedMode.wire)
            .putFloat("volume", masterVolume)
            .apply()
        val intent = Intent(this, HostService::class.java).apply {
            action = HostService.ACTION_START
            putExtra(HostService.EXTRA_NAME, deviceName)
            putExtra(HostService.EXTRA_MODE, selectedMode.wire)
            putExtra(HostService.EXTRA_VOLUME, masterVolume)
        }
        try {
            startForegroundService(intent)
            refreshHost()
        } catch (t: Throwable) {
            ServiceHub.hostStarting = false
            ServiceHub.hostError = "无法启动输出服务：${t.message}"
            toast(ServiceHub.hostError ?: "启动失败")
        }
    }

    private var hostStartedLocally = false

    private fun stopHost() {
        hostStartedLocally = false
        try {
            startService(Intent(this, HostService::class.java).apply {
                action = HostService.ACTION_STOP
            })
        } catch (t: Throwable) {
            toast("停止输出失败：${t.message}")
        }
        ServiceHub.hostStarting = false
    }

    private fun changeVolume(delta: Int) {
        val next = ((masterVolume * 100).toInt() + delta).coerceIn(0, 200)
        masterVolume = next / 100f
        hostVolumeValue.text = "$next%"
        prefs.edit().putFloat("volume", masterVolume).apply()
        if (ServiceHub.hostRunning) {
            try {
                startService(Intent(this, HostService::class.java).apply {
                    action = HostService.ACTION_SET_VOLUME
                    putExtra(HostService.EXTRA_VOLUME, masterVolume)
                })
            } catch (_: Exception) {}
        }
    }

    private fun scanHosts() {
        if (scanning) return
        if (ServiceHub.source != null || ServiceHub.shareStarting) {
            toast("请先停止当前共享")
            return
        }
        if (ServiceHub.hostRunning || ServiceHub.hostStarting) {
            toast("当前正在作为输出端，不能同时共享本机声音")
            return
        }
        scanning = true
        refreshHostsList()
        executor.execute {
            val found = runCatching { discoverHosts() }.getOrElse { emptyList() }
            handler.post {
                scanning = false
                hosts = found
                shareHostsContainer.tag = null
                refreshHostsList()
                if (found.isEmpty()) toast("未发现输出端，请检查对方是否已启动输出")
            }
        }
    }

    private fun discoverHosts(): List<HostOffer> {
        val found = linkedMapOf<String, HostOffer>()
        val socket = DatagramSocket()
        try {
            socket.broadcast = true
            socket.soTimeout = 320
            val query = Discovery.query()
            for (address in NetUtil.broadcastAddresses()) {
                try {
                    socket.send(DatagramPacket(query, query.size, address, Protocol.DISCOVERY_PORT))
                } catch (_: Exception) {}
            }
            val deadline = System.currentTimeMillis() + 3200
            val buffer = ByteArray(2048)
            while (System.currentTimeMillis() < deadline) {
                try {
                    val packet = DatagramPacket(buffer, buffer.size)
                    socket.receive(packet)
                    val offer = Discovery.parseOffer(
                        packet.data.copyOf(packet.length),
                        packet.address?.hostAddress ?: continue
                    ) ?: continue
                    found["${offer.roomId}|${offer.address}|${offer.tcpPort}"] = offer
                } catch (_: SocketTimeoutException) {
                    // Check deadline and keep listening.
                } catch (_: Exception) {
                    break
                }
            }
        } finally {
            try { socket.close() } catch (_: Exception) {}
        }
        return found.values.sortedBy { it.name }
    }

    private fun connectManual() {
        if (ServiceHub.hostRunning || ServiceHub.hostStarting) {
            toast("当前正在作为输出端，请先停止输出")
            return
        }
        if (ServiceHub.source != null || ServiceHub.shareStarting) {
            toast("已经在共享中")
            return
        }
        val raw = manualIp.text.toString().trim()
        val hostPart = raw.substringBefore(':').trim()
        val portPart = raw.substringAfter(':', "").trim()
        if (!Patterns.IP_ADDRESS.matcher(hostPart).matches()) {
            toast("请输入有效的 IPv4 地址")
            return
        }
        val port = portPart.toIntOrNull() ?: Protocol.CONTROL_PORT
        if (port !in 1..65535) {
            toast("端口无效")
            return
        }
        val address = try { InetAddress.getByName(hostPart).hostAddress ?: hostPart } catch (_: Exception) {
            toast("地址无法解析")
            return
        }
        prefs.edit().putString("lastManualIp", raw).apply()
        val offer = HostOffer(
            roomId = "manual", name = "手动输出端 $address", platform = "unknown",
            address = address, tcpPort = port, mode = selectedMode, sources = 0, pinRequired = false
        )
        beginConnect(offer)
    }

    private fun beginConnect(offer: HostOffer) {
        if (ServiceHub.hostRunning || ServiceHub.hostStarting) {
            toast("当前正在作为输出端，不能同时共享本机声音")
            return
        }
        if (ServiceHub.source != null || ServiceHub.shareStarting) {
            toast("已经在共享中")
            return
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            pendingHost = offer
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQ_RECORD_AUDIO)
            return
        }
        pendingHost = offer
        launchProjectionConsent()
    }

    private fun launchProjectionConsent() {
        val host = pendingHost
        if (host == null) return
        pendingHost = host
        val manager = getSystemService(MediaProjectionManager::class.java)
        try {
            startActivityForResult(manager.createScreenCaptureIntent(), REQ_PROJECTION)
        } catch (t: Throwable) {
            pendingHost = null
            toast("无法打开录屏授权：${t.message}")
        }
    }

    private fun startShareService(resultCode: Int, data: Intent) {
        val host = pendingHost ?: return
        pendingHost = null
        ServiceHub.clearShareError()
        ServiceHub.shareStarting = true
        val intent = Intent(this, ShareService::class.java).apply {
            action = ShareService.ACTION_START
            putExtra(ShareService.EXTRA_ADDRESS, host.address)
            putExtra(ShareService.EXTRA_PORT, host.tcpPort)
            putExtra(ShareService.EXTRA_HOST_NAME, host.name)
            putExtra(ShareService.EXTRA_MODE, host.mode.wire)
            putExtra(ShareService.EXTRA_NAME, deviceName)
            putExtra(ShareService.EXTRA_RESULT_CODE, resultCode)
            putExtra(ShareService.EXTRA_PROJECTION, data)
        }
        try {
            startForegroundService(intent)
            refreshShare()
        } catch (t: Throwable) {
            ServiceHub.shareStarting = false
            ServiceHub.shareError = "无法启动共享服务：${t.message}"
            toast(ServiceHub.shareError ?: "启动失败")
        }
    }

    private fun stopSharing() {
        pendingHost = null
        try {
            startService(Intent(this, ShareService::class.java).apply {
                action = ShareService.ACTION_STOP
            })
        } catch (t: Throwable) {
            toast("停止共享失败：${t.message}")
        }
        ServiceHub.shareStarting = false
    }

    // ---------------------------------------------------------------- permissions

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_RECORD_AUDIO) {
            val granted = grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED
            if (granted) {
                launchProjectionConsent()
            } else {
                pendingHost = null
                toast("需要「录音」权限才能捕获本机声音（不会录制麦克风）")
            }
        }
    }

    @Deprecated("Deprecated in Android")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ_PROJECTION) {
            if (resultCode == RESULT_OK && data != null && pendingHost != null) {
                startShareService(resultCode, data)
            } else {
                pendingHost = null
                toast("未授权录屏，无法捕获内部音频")
            }
        }
    }

    // ---------------------------------------------------------------- settings

    private fun showSettingsDialog() {
        val box = vertical().apply {
            setPadding(dp(20), dp(8), dp(20), 0)
        }
        box.addView(text("设备名称", 12f, Palette.TEXT, medium = true))
        val edit = EditText(this).apply {
            setText(deviceName)
            setSingleLine(true)
            setTextColor(Palette.TEXT)
            setHintTextColor(Palette.MUTED)
            textSize = 14f
            setPadding(dp(12), dp(10), dp(12), dp(10))
            background = roundedBackground(Palette.INPUT, 10, 0xFF263047.toInt())
            margins(top = 8)
        }
        box.addView(edit)
        box.addView(text("名称会显示在对方设备列表中。作为输出端或音源后，修改会在下次启动时生效。", 9.5f, Palette.MUTED).apply {
            margins(top = 8)
            setLineSpacing(0f, 1.3f)
        })
        box.addView(divider())
        box.addView(text(
            "同声 SameVoice 1.1.10\n" +
                "音频 48 kHz · 立体声 · PCM 16-bit\n" +
                "共享音频模式，不请求独占音频焦点\n" +
                "所有数据仅在同一局域网内传输",
            9.5f, Palette.MUTED
        ).apply { setLineSpacing(0f, 1.4f) })

        val dialog = AlertDialog.Builder(this)
            .setTitle("设置")
            .setView(ScrollView(this).apply { addView(box) })
            .setPositiveButton("保存", null)
            .setNegativeButton("取消", null)
            .setNeutralButton("系统设置", null)
            .create()

        val screamButton = secondaryButton("Scream 虚拟声卡接收")
        screamButton.margins(top = 4)
        screamButton.setOnClickListener {
            startActivity(Intent(this, ScreamActivity::class.java))
            dialog.dismiss()
        }
        box.addView(divider())
        box.addView(screamButton)
        box.addView(text(
            "独立接收 Windows Scream 虚拟声卡发送的 UDP PCM；不使用同声协议。",
            9.5f, Palette.MUTED
        ).apply {
            margins(top = 7)
            setLineSpacing(0f, 1.3f)
        })

        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val value = edit.text.toString().trim()
                if (value.isEmpty() || value.length > 40) {
                    toast("设备名称需为 1–40 个字符")
                    return@setOnClickListener
                }
                deviceName = value
                prefs.edit().putString("deviceName", value).apply()
                toast("设备名称已保存")
                dialog.dismiss()
            }
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener {
                try {
                    startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                        data = Uri.parse("package:$packageName")
                    })
                } catch (_: Exception) {}
            }
        }
        dialog.show()
    }
}

private fun Int.toColor(): Int = this
