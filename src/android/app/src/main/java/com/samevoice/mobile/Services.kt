package com.samevoice.mobile

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

object ServiceHub {
    @Volatile var host: HostEngine? = null
    @Volatile var hostRunning = false
    @Volatile var hostStarting = false
    @Volatile var hostError: String? = null
    @Volatile var source: SourceEngine? = null
    @Volatile var shareRunning = false
    @Volatile var shareStarting = false
    @Volatile var shareError: String? = null
    @Volatile var lastMessage: String = ""

    fun clearHostError() {
        hostError = null
    }

    fun clearShareError() {
        shareError = null
    }
}

private const val CHANNEL_HOST = "samevoice_host"
private const val CHANNEL_SHARE = "samevoice_share"

private fun createChannel(context: Context, id: String, name: String) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        val manager = context.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(id) == null) {
            manager.createNotificationChannel(
                NotificationChannel(id, name, NotificationManager.IMPORTANCE_LOW).apply {
                    description = "SameVoice 局域网音频"
                    setShowBadge(false)
                    enableVibration(false)
                }
            )
        }
    }
}

private fun openAppIntent(context: Context): PendingIntent {
    val intent = Intent(context, MainActivity::class.java).apply {
        flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
    }
    return PendingIntent.getActivity(
        context, 20, intent,
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
    )
}

private fun stopActionIntent(context: Context, serviceClass: Class<*>, requestCode: Int): PendingIntent {
    val intent = Intent(context, serviceClass).apply { action = "STOP" }
    return PendingIntent.getService(
        context, requestCode, intent,
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
    )
}

class HostService : Service() {
    companion object {
        const val ACTION_START = "START"
        const val ACTION_STOP = "STOP"
        const val ACTION_SET_MODE = "SET_MODE"
        const val ACTION_SET_VOLUME = "SET_VOLUME"
        const val EXTRA_NAME = "name"
        const val EXTRA_MODE = "mode"
        const val EXTRA_VOLUME = "volume"
        private const val NOTIFICATION_ID = 2001
        @Volatile var isRunning = false
    }

    private val worker = Executors.newSingleThreadExecutor()
    private val monitorStarted = AtomicBoolean(false)
    private var powerLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var notifier: Thread? = null
    private var starting = false
    private var stopping = false

    override fun onCreate() {
        super.onCreate()
        createChannel(this, CHANNEL_HOST, getString(R.string.channel_host))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopEverything()
                return START_NOT_STICKY
            }
            ACTION_SET_MODE -> {
                ServiceHub.host?.setMode(HostMode.fromWire(intent.getStringExtra(EXTRA_MODE)))
                return START_NOT_STICKY
            }
            ACTION_SET_VOLUME -> {
                ServiceHub.host?.setMasterVolume(intent.getFloatExtra(EXTRA_VOLUME, 0.8f))
                return START_NOT_STICKY
            }
            ACTION_START -> {
                try {
                    startHost(intent)
                } catch (t: Throwable) {
                    CrashStore.save(this, Thread.currentThread(), t)
                    ServiceHub.hostStarting = false
                    ServiceHub.hostError = "无法启动输出服务：${t.message ?: t.javaClass.simpleName}"
                    stopEverything()
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun startHost(intent: Intent) {
        if (isRunning || starting) return
        starting = true
        stopping = false
        ServiceHub.clearHostError()
        ServiceHub.hostStarting = true
        val name = intent.getStringExtra(EXTRA_NAME)?.takeIf { it.isNotBlank() } ?: NetUtil.defaultDeviceName()
        val mode = HostMode.fromWire(intent.getStringExtra(EXTRA_MODE))
        val initialVolume = intent.getFloatExtra(EXTRA_VOLUME, 0.8f).coerceIn(0f, 1f)

        try {
            startForeground(
                NOTIFICATION_ID,
                buildNotification("正在启动输出…", 0, null),
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            )
        } catch (t: Throwable) {
            CrashStore.save(this, Thread.currentThread(), t)
            starting = false
            ServiceHub.hostStarting = false
            ServiceHub.hostError = "无法进入前台服务：${t.message ?: t.javaClass.simpleName}"
            releaseLocks()
            stopSelf()
            return
        }
        acquireLocks()
        worker.execute {
            try {
                val engine = HostEngine(name, mode)
                engine.start()
                engine.setMasterVolume(initialVolume)
                if (stopping) {
                    engine.stop()
                    return@execute
                }
                ServiceHub.host = engine
                ServiceHub.hostRunning = true
                ServiceHub.hostStarting = false
                ServiceHub.lastMessage = "输出端已启动，等待音源接入。"
                isRunning = true
                starting = false
                updateNotification("输出端已启动，等待音源接入。", 0, null)
                startMonitor()
            } catch (t: Throwable) {
                starting = false
                ServiceHub.hostStarting = false
                val message = t.message ?: t.javaClass.simpleName
                CrashStore.save(this, Thread.currentThread(), t)
                ServiceHub.hostError = "输出启动失败：$message"
                ServiceHub.lastMessage = "输出启动失败：$message"
                try {
                    updateNotification("输出启动失败", 0, message)
                } catch (_: Exception) {}
                Handler(Looper.getMainLooper()).postDelayed({ stopEverything() }, 1800)
            }
        }
    }

    private fun startMonitor() {
        if (!monitorStarted.compareAndSet(false, true)) return
        notifier = Thread({
            var lastCount = -1
            while (isRunning && !stopping) {
                try { Thread.sleep(1500) } catch (_: InterruptedException) { break }
                val snapshot = ServiceHub.host?.snapshot() ?: break
                val count = snapshot.sourceCount
                if (count != lastCount) {
                    lastCount = count
                    val text = if (count == 0) "等待音源接入" else "$count 路音源正在混音"
                    updateNotification("同声正在输出", count, text)
                }
                if (snapshot.lastError != null) {
                    ServiceHub.hostError = "音频输出失败：${snapshot.lastError}"
                    Handler(Looper.getMainLooper()).post { stopEverything() }
                    break
                }
            }
        }, "sv-host-notifier").apply {
            isDaemon = true
            start()
        }
    }

    private fun buildNotification(title: String, count: Int, text: String?): Notification {
        val builder = Notification.Builder(this, CHANNEL_HOST)
            .setContentTitle(if (count > 0) "同声 · $count 路混音中" else title)
            .setContentText(text ?: "与其他应用共同播放 · 不抢占音频焦点")
            .setSmallIcon(R.drawable.ic_stat_wave)
            .setContentIntent(openAppIntent(this))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(
                Notification.Action.Builder(
                    Icon.createWithResource(this, R.drawable.ic_stat_wave),
                    getString(R.string.notification_stop),
                    stopActionIntent(this, HostService::class.java, 11)
                ).build()
            )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        }
        return builder.build()
    }

    private fun updateNotification(title: String, count: Int, text: String?) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(title, count, text))
    }

    private fun acquireLocks() {
        try {
            val power = getSystemService(PowerManager::class.java)
            powerLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SameVoice:Host").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}
        try {
            val wifi = applicationContext.getSystemService(WifiManager::class.java)
            wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "SameVoice:Host").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}
    }

    private fun releaseLocks() {
        try { powerLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        powerLock = null
        wifiLock = null
    }

    private fun stopEverything() {
        if (stopping) return
        stopping = true
        isRunning = false
        ServiceHub.hostRunning = false
        ServiceHub.hostStarting = false
        try { ServiceHub.host?.stop() } catch (_: Exception) {}
        ServiceHub.host = null
        releaseLocks()
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (_: Exception) {}
        stopSelf()
    }

    override fun onDestroy() {
        stopping = true
        isRunning = false
        ServiceHub.hostRunning = false
        ServiceHub.hostStarting = false
        try { ServiceHub.host?.stop() } catch (_: Exception) {}
        ServiceHub.host = null
        releaseLocks()
        worker.shutdownNow()
        notifier?.interrupt()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}

class ShareService : Service() {
    companion object {
        const val ACTION_START = "START"
        const val ACTION_STOP = "STOP"
        const val EXTRA_ADDRESS = "address"
        const val EXTRA_PORT = "port"
        const val EXTRA_HOST_NAME = "hostName"
        const val EXTRA_MODE = "mode"
        const val EXTRA_NAME = "name"
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_PROJECTION = "projection"
        private const val NOTIFICATION_ID = 2002
        @Volatile var isRunning = false
    }

    private val worker = Executors.newSingleThreadExecutor()
    private val monitorStarted = AtomicBoolean(false)
    private var powerLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var projection: MediaProjection? = null
    private var capture: PcmCapture? = null
    private var monitor: Thread? = null
    private var notifier: Thread? = null
    private var starting = false
    private var stopping = false

    override fun onCreate() {
        super.onCreate()
        createChannel(this, CHANNEL_SHARE, getString(R.string.channel_share))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopEverything()
                return START_NOT_STICKY
            }
            ACTION_START -> {
                try {
                    startSharing(intent)
                } catch (t: Throwable) {
                    CrashStore.save(this, Thread.currentThread(), t)
                    ServiceHub.shareStarting = false
                    ServiceHub.shareError = "无法启动共享服务：${t.message ?: t.javaClass.simpleName}"
                    stopEverything()
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun startSharing(intent: Intent) {
        if (starting || isRunning) return
        starting = true
        stopping = false
        ServiceHub.clearShareError()
        ServiceHub.shareStarting = true

        // Must enter the mediaProjection foreground service before the
        // MediaProjection object is used on Android 14+.
        try {
            startForeground(
                NOTIFICATION_ID,
                buildNotification("正在连接输出端…", null),
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            )
        } catch (t: Throwable) {
            CrashStore.save(this, Thread.currentThread(), t)
            starting = false
            ServiceHub.shareStarting = false
            ServiceHub.shareError = "无法进入前台服务：${t.message ?: t.javaClass.simpleName}"
            releaseLocks()
            stopSelf()
            return
        }
        acquireLocks()

        val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
        @Suppress("DEPRECATION")
        val projectionData = intent.getParcelableExtra<Intent>(EXTRA_PROJECTION)
        if (projectionData == null) {
            fail("屏幕录制授权数据缺失，请重新发起共享。")
            return
        }
        val address = intent.getStringExtra(EXTRA_ADDRESS) ?: run {
            fail("输出端地址缺失。")
            return
        }
        val port = intent.getIntExtra(EXTRA_PORT, Protocol.CONTROL_PORT)
        val hostName = intent.getStringExtra(EXTRA_HOST_NAME) ?: "SameVoice 输出端"
        val mode = HostMode.fromWire(intent.getStringExtra(EXTRA_MODE))
        val name = intent.getStringExtra(EXTRA_NAME)?.takeIf { it.isNotBlank() } ?: NetUtil.defaultDeviceName()

        val projectionManager = getSystemService(MediaProjectionManager::class.java)
        try {
            projection = projectionManager.getMediaProjection(resultCode, projectionData)
        } catch (t: Throwable) {
            fail("获取屏幕录制授权失败：${t.message}")
            return
        }
        val activeProjection = projection ?: run {
            fail("屏幕录制授权无效。")
            return
        }
        activeProjection.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                if (!stopping) {
                    Handler(Looper.getMainLooper()).post {
                        ServiceHub.shareError = "系统已停止屏幕录制授权。"
                        stopEverything()
                    }
                }
            }
        }, Handler(Looper.getMainLooper()))

        val audioCapture = try {
            MediaProjectionAudioCapture(activeProjection)
        } catch (t: Throwable) {
            fail("无法创建内部音频采集：${t.message}")
            return
        }
        capture = audioCapture
        val engine = SourceEngine(name)
        ServiceHub.source = engine
        val offer = HostOffer(
            roomId = "", name = hostName, platform = "android",
            address = address, tcpPort = port, mode = mode, sources = 0, pinRequired = false
        )

        worker.execute {
            try {
                engine.connectAndStart(offer, audioCapture)
                if (stopping) {
                    engine.stop()
                    return@execute
                }
                ServiceHub.shareRunning = true
                isRunning = true
                starting = false
                ServiceHub.shareStarting = false
                updateNotification("正在共享本机声音", engine.status())
                startMonitor()
            } catch (t: Throwable) {
                starting = false
                ServiceHub.shareStarting = false
                CrashStore.save(this, Thread.currentThread(), t)
                val message = ServiceHub.source?.lastError ?: t.message ?: t.javaClass.simpleName
                ServiceHub.shareError = "共享失败：$message"
                try { updateNotification("共享失败", null) } catch (_: Exception) {}
                Handler(Looper.getMainLooper()).postDelayed({ stopEverything() }, 1200)
            }
        }
    }

    private fun startMonitor() {
        if (!monitorStarted.compareAndSet(false, true)) return
        monitor = Thread({
            var lastText = ""
            while (isRunning && !stopping) {
                try { Thread.sleep(1800) } catch (_: InterruptedException) { break }
                val engine = ServiceHub.source ?: break
                val status = engine.status()
                if (status.state == ShareState.ERROR) {
                    ServiceHub.shareError = status.message
                    Handler(Looper.getMainLooper()).post { stopEverything() }
                    break
                }
                val text = if (status.state == ShareState.SHARING) {
                    val rtt = if (status.rttMs >= 0) " · ${"%.0f".format(status.rttMs)} ms" else ""
                    "${status.hostName}$rtt"
                } else status.message
                if (text != lastText) {
                    lastText = text
                    updateNotification("正在共享本机声音", status)
                }
            }
        }, "sv-share-monitor").apply {
            isDaemon = true
            start()
        }
    }

    private fun buildNotification(title: String, status: ShareStatus?): Notification {
        val text = status?.let {
            if (it.isSharing) "已连接 ${it.hostName} · 关闭应用后仍会继续" else it.message
        } ?: "授权内部音频并发送到输出端"
        val builder = Notification.Builder(this, CHANNEL_SHARE)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_stat_wave)
            .setContentIntent(openAppIntent(this))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(
                Notification.Action.Builder(
                    Icon.createWithResource(this, R.drawable.ic_stat_wave),
                    getString(R.string.notification_stop),
                    stopActionIntent(this, ShareService::class.java, 12)
                ).build()
            )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        }
        return builder.build()
    }

    private fun updateNotification(title: String, status: ShareStatus?) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(title, status))
    }

    private fun fail(message: String) {
        starting = false
        ServiceHub.shareError = message
        ServiceHub.lastMessage = message
        try { updateNotification("共享失败", null) } catch (_: Exception) {}
        Handler(Looper.getMainLooper()).postDelayed({ stopEverything() }, 1500)
    }

    private fun acquireLocks() {
        try {
            val power = getSystemService(PowerManager::class.java)
            powerLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SameVoice:Share").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}
        try {
            val wifi = applicationContext.getSystemService(WifiManager::class.java)
            wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "SameVoice:Share").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}
    }

    private fun releaseLocks() {
        try { powerLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        powerLock = null
        wifiLock = null
    }

    private fun stopEverything() {
        if (stopping) return
        stopping = true
        starting = false
        isRunning = false
        ServiceHub.shareRunning = false
        ServiceHub.shareStarting = false
        try { ServiceHub.source?.stop() } catch (_: Exception) {}
        ServiceHub.source = null
        try { capture?.stop() } catch (_: Exception) {}
        capture = null
        try { projection?.stop() } catch (_: Exception) {}
        projection = null
        releaseLocks()
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (_: Exception) {}
        stopSelf()
    }

    override fun onDestroy() {
        stopping = true
        isRunning = false
        ServiceHub.shareRunning = false
        ServiceHub.shareStarting = false
        try { ServiceHub.source?.stop() } catch (_: Exception) {}
        ServiceHub.source = null
        try { capture?.stop() } catch (_: Exception) {}
        capture = null
        try { projection?.stop() } catch (_: Exception) {}
        projection = null
        releaseLocks()
        worker.shutdownNow()
        monitor?.interrupt()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
