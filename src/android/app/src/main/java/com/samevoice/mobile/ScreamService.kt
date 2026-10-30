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
import android.os.Build
import android.os.IBinder
import java.util.concurrent.Executors

private const val CHANNEL_SCREAM = "samevoice_scream"

class ScreamService : Service() {
    companion object {
        const val ACTION_START = "START"
        const val ACTION_STOP = "STOP"
        private const val NOTIFICATION_ID = 2002
    }

    private val worker = Executors.newSingleThreadExecutor()
    private var receiver: ScreamReceiver? = null
    private var stopping = false

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            else -> {
                startForegroundNotification("正在启动 Scream 接收")
                if (ScreamHub.running || ScreamHub.starting) return START_NOT_STICKY
                ScreamHub.starting = true
                ScreamHub.error = null
                worker.execute {
                    val r = ScreamReceiver(applicationContext)
                    receiver = r
                    try {
                        r.start()
                        ScreamHub.starting = false
                        ScreamHub.running = true
                        updateNotification("正在接收 Scream 音频")
                    } catch (t: Throwable) {
                        ScreamHub.starting = false
                        ScreamHub.running = false
                        ScreamHub.error = t.message ?: t.javaClass.simpleName
                        ScreamHub.status = "Scream 接收失败"
                        updateNotification("Scream 接收失败")
                    }
                }
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stopping = true
        ScreamHub.running = false
        ScreamHub.starting = false
        ScreamHub.status = "已停止"
        try { receiver?.stop() } catch (_: Exception) {}
        receiver = null
        try { worker.shutdownNow() } catch (_: Exception) {}
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(NotificationManager::class.java)
            if (manager.getNotificationChannel(CHANNEL_SCREAM) == null) {
                manager.createNotificationChannel(
                    NotificationChannel(
                        CHANNEL_SCREAM, "Scream 接收", NotificationManager.IMPORTANCE_LOW
                    ).apply {
                        description = "Scream 虚拟声卡网络音频接收"
                        setShowBadge(false)
                        enableVibration(false)
                    }
                )
            }
        }
    }

    private fun openScreamIntent(context: Context): PendingIntent {
        val intent = Intent(context, ScreamActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        return PendingIntent.getActivity(
            context, 31, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    private fun stopIntent(context: Context): PendingIntent =
        PendingIntent.getService(
            context, 32,
            Intent(context, ScreamService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

    private fun buildNotification(title: String): Notification {
        val detail = when {
            ScreamHub.error != null -> "错误：${ScreamHub.error}"
            ScreamHub.running && ScreamHub.format.isNotEmpty() ->
                "${ScreamHub.format} · ${ScreamHub.packets} 包"
            ScreamHub.running -> "等待 Windows Scream 数据（UDP 4010）"
            else -> "Scream 虚拟声卡网络接收"
        }
        val builder = Notification.Builder(this, CHANNEL_SCREAM)
            .setContentTitle(title)
            .setContentText(detail)
            .setSmallIcon(R.drawable.ic_stat_wave)
            .setContentIntent(openScreamIntent(this))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(
                Notification.Action.Builder(
                    Icon.createWithResource(this, R.drawable.ic_stat_wave),
                    "停止接收",
                    stopIntent(this)
                ).build()
            )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        }
        return builder.build()
    }

    private fun startForegroundNotification(title: String) {
        val notification = buildNotification(title)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NOTIFICATION_ID, notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun updateNotification(title: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(title))
    }
}
