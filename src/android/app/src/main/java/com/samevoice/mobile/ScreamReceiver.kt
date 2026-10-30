package com.samevoice.mobile

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.net.wifi.WifiManager
import android.os.PowerManager
import android.os.Process
import java.net.DatagramPacket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.MulticastSocket
import java.net.NetworkInterface
import java.net.SocketAddress
import java.net.SocketTimeoutException
import java.util.Collections
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.max

object ScreamHub {
    @Volatile var running = false
    @Volatile var starting = false
    @Volatile var error: String? = null
    @Volatile var status = "未启动"
    @Volatile var format = ""
    @Volatile var packets = 0L
    @Volatile var bytes = 0L
    @Volatile var level = 0.0
    @Volatile var lastPacketMs = 0L
}

/**
 * Receives the Scream virtual sound card multicast PCM stream and plays it on
 * the Android default media output. Scream's driver sends UDP to
 * 239.255.77.77:4010 with a five-byte format header followed by raw PCM.
 */
class ScreamReceiver(context: Context) {
    private val appContext = context.applicationContext
    private val running = AtomicBoolean(false)
    private var socket: MulticastSocket? = null
    private var thread: Thread? = null
    @Volatile private var track: AudioTrack? = null
    private var trackFormat: ScreamFormat? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var powerLock: PowerManager.WakeLock? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        try {
            acquireLocks()
            val nic = findMulticastInterface()
                ?: throw IllegalStateException("没有可用的 Wi-Fi/局域网接口")
            val group = InetAddress.getByName(ScreamProtocol.GROUP_ADDRESS)
            val s = MulticastSocket(null as SocketAddress?)
            s.reuseAddress = true
            s.bind(InetSocketAddress(ScreamProtocol.PORT))
            s.networkInterface = nic
            s.timeToLive = 1
            s.soTimeout = 1000
            s.joinGroup(InetSocketAddress(group, ScreamProtocol.PORT), nic)
            socket = s

            ScreamHub.error = null
            ScreamHub.format = ""
            ScreamHub.packets = 0
            ScreamHub.bytes = 0
            ScreamHub.level = 0.0
            ScreamHub.lastPacketMs = 0
            ScreamHub.status = "等待 Scream 数据（UDP ${ScreamProtocol.PORT}）"
            ScreamHub.running = true

            thread = Thread({ receiveLoop(s) }, "sv-scream-receive").apply {
                priority = Thread.MAX_PRIORITY
                isDaemon = true
                start()
            }
        } catch (t: Throwable) {
            ScreamHub.error = t.message ?: t.javaClass.simpleName
            ScreamHub.status = "Scream 接收启动失败"
            ScreamHub.running = false
            running.set(false)
            closeSocket()
            releaseLocks()
            throw t
        }
    }

    fun stop() {
        if (!running.getAndSet(false)) return
        ScreamHub.running = false
        ScreamHub.status = "已停止"
        closeSocket()
        try { thread?.join(1200) } catch (_: InterruptedException) {}
        thread = null
        releaseTrack()
        releaseLocks()
    }

    private fun receiveLoop(s: MulticastSocket) {
        val buffer = ByteArray(ScreamProtocol.MAX_PACKET)
        var current: ScreamFormat? = null
        var invalid = 0L
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        } catch (_: Throwable) {
            try { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO) } catch (_: Throwable) {}
        }
        while (running.get()) {
            try {
                val packet = DatagramPacket(buffer, buffer.size)
                s.receive(packet)
                if (packet.length <= ScreamProtocol.HEADER_SIZE) {
                    invalid++
                    continue
                }
                val parsed = ScreamProtocol.parseFormat(buffer, packet.length)
                if (parsed == null) {
                    invalid++
                    if (invalid == 1L) ScreamHub.status = "收到非 Scream 数据包，等待格式头…"
                    continue
                }
                val pcm = ScreamProtocol.toStereo16(buffer, packet.length, parsed)
                if (pcm == null || pcm.isEmpty()) {
                    invalid++
                    continue
                }
                if (current != parsed) {
                    ensureTrack(parsed)
                    current = parsed
                }
                val audio = track ?: continue
                val written = audio.write(pcm, 0, pcm.size, AudioTrack.WRITE_BLOCKING)
                if (written < 0) throw IllegalStateException("AudioTrack 写入失败：$written")

                ScreamHub.running = true
                ScreamHub.format = parsed.description
                ScreamHub.packets++
                ScreamHub.bytes += packet.length.toLong()
                ScreamHub.level = ScreamProtocol.peakLevel(pcm)
                ScreamHub.lastPacketMs = System.currentTimeMillis()
                ScreamHub.status = "正在接收 Scream 音频"
            } catch (_: SocketTimeoutException) {
                // No packet for a second. Keep the socket alive so a paused
                // Windows stream can resume without restarting the service.
                if (ScreamHub.lastPacketMs == 0L) {
                    ScreamHub.status = "等待 Scream 数据（UDP ${ScreamProtocol.PORT}）"
                }
            } catch (t: Throwable) {
                if (running.get()) {
                    ScreamHub.error = t.message ?: t.javaClass.simpleName
                    ScreamHub.status = "Scream 接收中断"
                }
                break
            }
        }
    }

    private fun ensureTrack(format: ScreamFormat) {
        if (track != null && trackFormat == format) return
        releaseTrack()
        val min = AudioTrack.getMinBufferSize(
            format.sampleRate,
            AudioFormat.CHANNEL_OUT_STEREO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (min <= 0) throw IllegalStateException("设备不支持 ${format.sampleRate} Hz 立体声输出")
        val bufferBytes = max(min, format.sampleRate * 4 / 5) // ~200 ms of stereo PCM16
        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val audioFormat = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(format.sampleRate)
            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
            .build()
        val created = AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(audioFormat)
            .setBufferSizeInBytes(bufferBytes)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        if (created.state != AudioTrack.STATE_INITIALIZED) {
            created.release()
            throw IllegalStateException("无法创建 ${format.sampleRate} Hz AudioTrack")
        }
        created.play()
        track = created
        trackFormat = format
    }

    private fun releaseTrack() {
        try { track?.pause() } catch (_: Exception) {}
        try { track?.flush() } catch (_: Exception) {}
        try { track?.release() } catch (_: Exception) {}
        track = null
        trackFormat = null
    }

    private fun closeSocket() {
        try { socket?.close() } catch (_: Exception) {}
        socket = null
    }

    private fun findMulticastInterface(): NetworkInterface? {
        val all = Collections.list(NetworkInterface.getNetworkInterfaces())
        val active = all.filter { it.isUp && !it.isLoopback && hasIpv4(it) }
        return active.firstOrNull { it.name.startsWith("wlan") }
            ?: active.firstOrNull()
    }

    private fun hasIpv4(nic: NetworkInterface): Boolean =
        Collections.list(nic.inetAddresses).any { it is Inet4Address }

    private fun acquireLocks() {
        try {
            val wifi = appContext.getSystemService(WifiManager::class.java)
            multicastLock = wifi.createMulticastLock("SameVoice:Scream").apply {
                setReferenceCounted(false)
                acquire()
            }
            wifiLock = wifi.createWifiLock(
                WifiManager.WIFI_MODE_FULL_HIGH_PERF, "SameVoice:Scream"
            ).apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}
        try {
            val power = appContext.getSystemService(PowerManager::class.java)
            powerLock = power.newWakeLock(
                PowerManager.PARTIAL_WAKE_LOCK, "SameVoice:Scream"
            ).apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}
    }

    private fun releaseLocks() {
        try { multicastLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { powerLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        multicastLock = null
        wifiLock = null
        powerLock = null
    }
}
