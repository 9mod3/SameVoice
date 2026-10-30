package com.samevoice.mobile

import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.max

enum class ShareState { IDLE, CONNECTING, SYNCING, SHARING, ERROR }

data class ShareStatus(
    val state: ShareState,
    val deviceName: String,
    val hostName: String,
    val hostAddress: String,
    val mode: HostMode,
    val message: String,
    val rttMs: Double,
    val packetsSent: Long,
    val bytesSent: Long,
    val droppedPackets: Long,
    val isSharing: Boolean
)

/**
 * Android source engine: captures allowed internal audio, packetizes it, performs
 * NTP-style clock sync with the output host and streams 5 ms PCM over UDP.
 */
class SourceEngine(private val deviceName: String) {
    private val clock = ClockSync()
    private val sendQueue = LinkedBlockingQueue<ByteArray>(96)
    private val udpSendLock = Any()
    private val controlLock = Any()
    private val pendingLock = Any()
    private val pending = ShortArray(16_384)
    private var pendingCount = 0
    private val packetsSent = AtomicLong()
    private val bytesSent = AtomicLong()
    private val dropped = AtomicLong()
    private val cleanupOnce = AtomicBoolean(false)

    @Volatile private var state = ShareState.IDLE
    @Volatile private var hostName = ""
    @Volatile private var hostAddress = ""
    @Volatile private var mode = HostMode.BALANCED
    @Volatile private var message = ""
    @Volatile private var sharing = false
    @Volatile private var running = false
    @Volatile private var stopping = false
    @Volatile var lastError: String? = null
        private set

    private var socket: Socket? = null
    private var reader: BufferedReader? = null
    private var writer: BufferedWriter? = null
    private var udp: DatagramSocket? = null
    private var capture: PcmCapture? = null
    private var session = 0
    private var token = 0L
    private var sequence = 0
    private var pingSequence = 0
    private var lastPongRttUs = -1L
    private var nextPacketLocalUs = 0L
    private val threads = mutableListOf<Thread>()

    fun status(): ShareStatus = ShareStatus(
        state = state,
        deviceName = deviceName,
        hostName = hostName,
        hostAddress = hostAddress,
        mode = mode,
        message = message,
        rttMs = if (lastPongRttUs >= 0) lastPongRttUs / 1000.0 else -1.0,
        packetsSent = packetsSent.get(),
        bytesSent = bytesSent.get(),
        droppedPackets = dropped.get(),
        isSharing = sharing
    )

    /**
     * Blocking setup. Must run on a background thread.
     */
    fun connectAndStart(offer: HostOffer, pcmCapture: PcmCapture) {
        if (state != ShareState.IDLE) throw IllegalStateException("共享已经启动")
        if (stopping) throw IllegalStateException("共享已取消")
        hostName = offer.name
        hostAddress = offer.address
        mode = offer.mode
        lastError = null
        cleanupOnce.set(false)
        setState(ShareState.CONNECTING, "正在连接输出端…")

        try {
            val tcp = Socket()
            tcp.tcpNoDelay = true
            tcp.connect(InetSocketAddress(offer.address, offer.tcpPort), 5000)
            tcp.soTimeout = 7000
            socket = tcp
            reader = BufferedReader(InputStreamReader(tcp.getInputStream(), Charsets.UTF_8))
            writer = BufferedWriter(OutputStreamWriter(tcp.getOutputStream(), Charsets.UTF_8))

            writeControl(Control.hello(deviceName, "android"))
            val welcomeLine = reader?.readLine() ?: throw IllegalStateException("输出端关闭了连接")
            val welcome = Control.parseWelcome(welcomeLine)
                ?: throw IllegalStateException(Control.parseError(welcomeLine) ?: "输出端返回了无效握手")
            session = welcome.session
            token = welcome.token
            hostName = welcome.hostName
            mode = welcome.mode
            tcp.soTimeout = 0
            running = true

            val audio = DatagramSocket()
            audio.connect(InetSocketAddress(offer.address, welcome.audioPort))
            audio.soTimeout = 500
            audio.sendBufferSize = 256 * 1024
            udp = audio
            capture = pcmCapture

            startThread("sv-source-control") { controlLoop() }
            startThread("sv-source-pong") { pongLoop() }

            setState(ShareState.SYNCING, "正在校准设备时钟…")
            initialClockSync()
            if (stopping) throw IllegalStateException("共享已取消")
            if (!clock.isReady || clock.sampleCount < 4) {
                throw IllegalStateException("时钟校准超时，请检查 Wi-Fi 是否稳定")
            }

            sharing = true
            pcmCapture.start(::onPcm, ::onCaptureError)
            startThread("sv-source-send") { sendLoop() }
            startThread("sv-source-heartbeat") { heartbeatLoop() }
            startThread("sv-source-ping") { pingLoop() }
            writeControl(Control.simple("start"))
            setState(ShareState.SHARING, "正在共享本机声音")
        } catch (t: Throwable) {
            lastError = t.message ?: t.javaClass.simpleName
            setState(ShareState.ERROR, lastError ?: "共享失败")
            closeInternal(announce = false)
            throw t
        }
    }

    fun stop() {
        stopping = true
        sharing = false
        if (state != ShareState.IDLE) {
            try { writeControl(Control.simple("stop")) } catch (_: Exception) {}
            try { writeControl(Control.simple("bye")) } catch (_: Exception) {}
        }
        closeInternal(announce = true)
    }

    private fun closeInternal(announce: Boolean) {
        if (!cleanupOnce.compareAndSet(false, true)) return
        running = false
        sharing = false
        try { capture?.stop() } catch (_: Exception) {}
        capture = null
        try { socket?.close() } catch (_: Exception) {}
        try { udp?.close() } catch (_: Exception) {}
        socket = null
        udp = null
        sendQueue.clear()
        for (thread in threads) {
            if (thread == Thread.currentThread()) continue
            try { thread.join(500) } catch (_: InterruptedException) {}
        }
        threads.clear()
        reader = null
        writer = null
        if (announce) {
            setState(ShareState.IDLE, "已停止共享")
        }
    }

    private fun onPcm(samples: ShortArray, captureStartUs: Long) {
        if (!sharing || !clock.isReady) return
        if (udp == null) return
        synchronized(pendingLock) {
            // Keep a continuous sample clock. Re-anchoring on every AudioRecord
            // callback used to inject the read-callback jitter into the network
            // timestamps, which produced periodic clicks in the Windows mixer.
            // Only re-anchor when the capture stream actually gaped.
            if (nextPacketLocalUs == 0L ||
                captureStartUs > nextPacketLocalUs + 250_000 ||
                captureStartUs + 100_000 < nextPacketLocalUs
            ) {
                pendingCount = 0
                nextPacketLocalUs = captureStartUs
            }
            appendPending(samples)
            while (pendingCount >= Protocol.PACKET_FRAMES * Protocol.CHANNELS) {
                val payload = ByteBuffer.allocate(Protocol.PACKET_BYTES).order(ByteOrder.LITTLE_ENDIAN)
                for (i in 0 until Protocol.PACKET_FRAMES * Protocol.CHANNELS) {
                    payload.putShort(pending[i])
                }
                System.arraycopy(
                    pending,
                    Protocol.PACKET_FRAMES * Protocol.CHANNELS,
                    pending,
                    0,
                    pendingCount - Protocol.PACKET_FRAMES * Protocol.CHANNELS
                )
                pendingCount -= Protocol.PACKET_FRAMES * Protocol.CHANNELS

                val localStart = nextPacketLocalUs
                nextPacketLocalUs += Protocol.PACKET_MS * 1000L
                val seq = sequence++
                val hostCapture = clock.toHost(localStart)
                val packet = Packets.encode(
                    Protocol.TYPE_AUDIO, session, token, seq, hostCapture, payload.array()
                )
                if (!sendQueue.offer(packet)) {
                    dropped.incrementAndGet()
                } else {
                    packetsSent.incrementAndGet()
                    bytesSent.addAndGet(packet.size.toLong())
                }
            }
        }
    }

    private fun appendPending(samples: ShortArray) {
        if (pendingCount + samples.size > pending.size) {
            // Should not happen with 20 ms callbacks, but never lose sync.
            pendingCount = 0
        }
        System.arraycopy(samples, 0, pending, pendingCount, samples.size)
        pendingCount += samples.size
    }

    private fun onCaptureError(error: String) {
        if (stopping) return
        fatal("系统声音采集失败：$error")
    }

    private fun controlLoop() {
        val input = reader ?: return
        try {
            while (running) {
                val line = input.readLine() ?: run {
                    if (!stopping) fatal("输出端已断开")
                    return
                }
                if (line.isEmpty()) continue
                when (Control.type(line)) {
                    "started" -> {
                        message = "正在共享本机声音"
                    }
                    "mode" -> {
                        Control.parseMode(line)?.let { mode = it }
                    }
                    "error" -> {
                        fatal(Control.parseError(line) ?: "输出端返回错误")
                        return
                    }
                    "bye" -> {
                        if (!stopping) fatal("输出端已停止")
                        return
                    }
                }
            }
        } catch (_: SocketException) {
            if (running && !stopping) fatal("与输出端的连接已断开")
        } catch (_: Exception) {
            if (running && !stopping) fatal("与输出端的连接已断开")
        }
    }

    private fun pongLoop() {
        val audio = udp ?: return
        val buffer = ByteArray(2048)
        while (running) {
            try {
                val datagram = DatagramPacket(buffer, buffer.size)
                audio.receive(datagram)
                val packet = Packets.decode(datagram.data, datagram.length) ?: continue
                if (packet.type != Protocol.TYPE_PONG ||
                    packet.session != session ||
                    packet.token != token
                ) continue
                val times = Packets.pongTimes(packet) ?: continue
                val t4 = Protocol.nowUs()
                val rtt = (t4 - times.first) - (packet.captureUs - times.second)
                if (rtt < 0 || rtt > 1_000_000) continue
                clock.addSample(times.first, times.second, packet.captureUs, t4)
                lastPongRttUs = rtt
            } catch (_: java.net.SocketTimeoutException) {
                continue
            } catch (_: SocketException) {
                break
            } catch (_: Exception) {
                if (!running) break
            }
        }
    }

    private fun initialClockSync() {
        for (i in 0 until 8) {
            if (!running) break
            sendPing()
            try { Thread.sleep(70) } catch (_: InterruptedException) { break }
            if (clock.sampleCount >= 5) break
        }
        val deadline = System.currentTimeMillis() + 3000
        while (clock.sampleCount < 4 && running && System.currentTimeMillis() < deadline) {
            try { Thread.sleep(40) } catch (_: InterruptedException) { break }
        }
    }

    private fun pingLoop() {
        while (running) {
            try { Thread.sleep(4000) } catch (_: InterruptedException) { break }
            if (!running) break
            sendPing()
        }
    }

    private fun sendPing() {
        val audio = udp ?: return
        val seq = ++pingSequence
        val t1 = Protocol.nowUs()
        val ping = Packets.encode(Protocol.TYPE_PING, session, token, seq, t1, ByteArray(0))
        try {
            synchronized(udpSendLock) {
                audio.send(DatagramPacket(ping, ping.size))
            }
        } catch (_: Exception) {
        }
    }

    private fun sendLoop() {
        while (running) {
            val packet = try { sendQueue.poll(200, java.util.concurrent.TimeUnit.MILLISECONDS) } catch (_: InterruptedException) { break }
                ?: continue
            val audio = udp ?: break
            try {
                synchronized(udpSendLock) {
                    audio.send(DatagramPacket(packet, packet.size))
                }
            } catch (_: Exception) {
                dropped.incrementAndGet()
                if (!running) break
            }
        }
    }

    private fun heartbeatLoop() {
        while (running) {
            try { Thread.sleep(3000) } catch (_: InterruptedException) { break }
            if (!running) break
            try { writeControl(Control.simple("heartbeat")) } catch (_: Exception) { break }
        }
    }

    private fun writeControl(json: ByteArray) {
        val output = writer ?: return
        synchronized(controlLock) {
            output.write(String(json, Charsets.UTF_8))
            output.write("\n")
            output.flush()
        }
    }

    private fun fatal(error: String) {
        if (stopping) return
        lastError = error
        setState(ShareState.ERROR, error)
        closeInternal(announce = false)
    }

    private fun setState(value: ShareState, text: String) {
        state = value
        message = text
    }

    private fun startThread(name: String, body: () -> Unit) {
        val thread = Thread(body, name).apply {
            isDaemon = true
            start()
        }
        threads.add(thread)
    }
}
