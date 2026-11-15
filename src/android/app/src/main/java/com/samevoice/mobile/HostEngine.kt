package com.samevoice.mobile

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Process
import android.util.Log
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.abs
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.tanh

enum class HostUiState { STOPPED, RUNNING, ERROR }

data class HostSourceInfo(
    val id: Int,
    val name: String,
    val platform: String,
    val address: String,
    val sharing: Boolean,
    val bufferMs: Double,
    val volume: Float,
    val packets: Long,
    val bytes: Long,
    val missing: Long,
    val lastPacketAgeMs: Long
)

data class HostSnapshot(
    val running: Boolean,
    val hostName: String,
    val localAddresses: String,
    val controlPort: Int,
    val audioPort: Int,
    val mode: HostMode,
    val sourceCount: Int,
    val peak: Double,
    val masterVolume: Float,
    val sources: List<HostSourceInfo>,
    val lastError: String?
)

/**
 * Android output engine. Uses an AudioTrack in shared mode and deliberately does
 * not request audio focus, so other apps keep playing and are mixed by Android.
 */
class HostEngine(
    private val hostName: String,
    initialMode: HostMode,
    private val maxQuality: AudioQuality = AudioQuality.HIGH,
    initialBufferMs: Int? = null
) {
    private val mixer = MixEngine(initialMode).also {
        it.setBufferMs(initialBufferMs ?: initialMode.bufferMs)
    }
    private val sessions = ConcurrentHashMap<Int, SourceSession>()
    private val byToken = ConcurrentHashMap<Long, SourceSession>()
    private val nextId = AtomicInteger(SecureRandom().nextInt(100_000) + 1000)
    private val random = SecureRandom()
    private val roomId = ByteArray(6).also { random.nextBytes(it) }
        .joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    private val localAddresses = NetUtil.localAddressText()

    @Volatile private var running = false
    @Volatile var mode: HostMode = initialMode
        private set
    @Volatile var lastError: String? = null
        private set

    private var controlServer: ServerSocket? = null
    private var audioSocket: DatagramSocket? = null
    private var discoverySocket: DatagramSocket? = null
    private var audioTrack: AudioTrack? = null
    private var outputFloat = true
    private val threads = mutableListOf<Thread>()
    private val sendLock = Any()
    private var lastDiagMs = 0L

    val controlPort: Int get() = controlServer?.localPort ?: 0
    val audioPort: Int get() = audioSocket?.localPort ?: 0
    val isRunning: Boolean get() = running

    fun start() {
        if (running) return
        lastError = null
        try {
            controlServer = bindControl()
            audioSocket = bindAudio()
            discoverySocket = bindDiscovery()
            audioTrack = createTrack()
            val trackFrames = audioTrack?.bufferSizeInFrames ?: 0
            val latencyUs = if (trackFrames > 0)
                trackFrames * 1_000_000L / Protocol.SAMPLE_RATE else 0L
            mixer.start(latencyUs)
            running = true

            startThread("sv-host-control") { acceptLoop() }
            startThread("sv-host-audio") { audioReceiveLoop() }
            startThread("sv-host-discovery") { discoveryLoop() }
            startThread("sv-host-output") { outputLoop() }
            startThread("sv-host-maintenance") { maintenanceLoop() }
        } catch (t: Throwable) {
            lastError = t.message ?: t.javaClass.simpleName
            stop()
            throw t
        }
    }

    fun stop() {
        if (!running && controlServer == null && audioTrack == null) return
        running = false
        mixer.stop()
        try { controlServer?.close() } catch (_: Exception) {}
        try { audioSocket?.close() } catch (_: Exception) {}
        try { discoverySocket?.close() } catch (_: Exception) {}
        controlServer = null
        audioSocket = null
        discoverySocket = null

        for (session in sessions.values) {
            try { session.send(Control.simple("bye")) } catch (_: Exception) {}
            session.close()
        }
        sessions.clear()
        byToken.clear()
        try { audioTrack?.pause() } catch (_: Exception) {}
        try { audioTrack?.flush() } catch (_: Exception) {}
        try { audioTrack?.release() } catch (_: Exception) {}
        audioTrack = null
        for (thread in threads) {
            try { thread.join(400) } catch (_: InterruptedException) {}
        }
        threads.clear()
    }

    fun setMode(value: HostMode) {
        if (mode == value) return
        mode = value
        mixer.setMode(value)
        for (session in sessions.values) {
            try { session.send(Control.mode(value)) } catch (_: Exception) {}
        }
    }

    fun setBufferMs(ms: Int) {
        mixer.setBufferMs(ms)
    }

    fun setMasterVolume(volume: Float) {
        mixer.masterVolume = volume.coerceIn(0f, 2f)
    }

    fun setSourceVolume(id: Int, volume: Float) {
        mixer.setSourceVolume(id, volume)
    }

    fun snapshot(): HostSnapshot {
        val now = System.currentTimeMillis()
        val sourceInfo = sessions.values.map { session ->
            HostSourceInfo(
                id = session.id,
                name = session.name,
                platform = session.platform,
                address = session.address,
                sharing = session.sharing,
                bufferMs = mixer.getBufferAheadMs(session.id),
                volume = mixer.getSourceVolume(session.id),
                packets = session.packets.get(),
                bytes = session.bytes.get(),
                missing = session.missing.get(),
                lastPacketAgeMs = (now - session.lastSeenMs).coerceAtLeast(0)
            )
        }.sortedBy { it.id }
        return HostSnapshot(
            running = running,
            hostName = hostName,
            localAddresses = localAddresses,
            controlPort = controlPort,
            audioPort = audioPort,
            mode = mode,
            sourceCount = sessions.size,
            peak = mixer.peak,
            masterVolume = mixer.masterVolume,
            sources = sourceInfo,
            lastError = lastError
        )
    }

    // -------------------------------------------------------------- sockets

    private fun bindControl(): ServerSocket {
        var last: Exception? = null
        for (port in Protocol.CONTROL_PORT until Protocol.CONTROL_PORT + 10) {
            try {
                val server = ServerSocket()
                server.reuseAddress = true
                server.bind(InetSocketAddress(port))
                return server
            } catch (e: Exception) {
                last = e
            }
        }
        throw IllegalStateException("控制端口 47820–47829 均被占用", last)
    }

    private fun bindAudio(): DatagramSocket {
        val socket = DatagramSocket(null)
        socket.reuseAddress = true
        try {
            socket.bind(InetSocketAddress(Protocol.AUDIO_PORT))
        } catch (_: Exception) {
            socket.bind(InetSocketAddress(0))
        }
        socket.receiveBufferSize = 512 * 1024
        return socket
    }

    private fun bindDiscovery(): DatagramSocket {
        val socket = DatagramSocket(null)
        socket.reuseAddress = true
        socket.bind(InetSocketAddress(Protocol.DISCOVERY_PORT))
        socket.broadcast = true
        return socket
    }

    private fun createTrack(): AudioTrack {
        createFloatTrack()?.let {
            outputFloat = true
            return it
        }
        outputFloat = false
        return createPcm16Track()
    }

    private fun createFloatTrack(): AudioTrack? {
        val minBuffer = AudioTrack.getMinBufferSize(
            Protocol.SAMPLE_RATE,
            AudioFormat.CHANNEL_OUT_STEREO,
            AudioFormat.ENCODING_PCM_FLOAT
        )
        if (minBuffer <= 0) return null
        return try {
            buildTrack(
                AudioFormat.ENCODING_PCM_FLOAT,
                max(minBuffer, Protocol.PACKET_BYTES * 8)
            )?.takeIf { it.state == AudioTrack.STATE_INITIALIZED }?.also { it.play() }
        } catch (_: Throwable) {
            null
        }
    }

    private fun createPcm16Track(): AudioTrack {
        val minBuffer = AudioTrack.getMinBufferSize(
            Protocol.SAMPLE_RATE,
            AudioFormat.CHANNEL_OUT_STEREO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (minBuffer <= 0) throw IllegalStateException("设备不支持 48 kHz 立体声播放")
        val track = buildTrack(
            AudioFormat.ENCODING_PCM_16BIT,
            max(minBuffer, Protocol.PACKET_BYTES * 4)
        ) ?: throw IllegalStateException("无法初始化音频播放（AudioTrack）")
        if (track.state != AudioTrack.STATE_INITIALIZED) {
            try { track.release() } catch (_: Exception) {}
            throw IllegalStateException("无法初始化音频播放（AudioTrack）")
        }
        track.play()
        return track
    }

    private fun buildTrack(encoding: Int, bufferBytes: Int): AudioTrack? {
        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val format = AudioFormat.Builder()
            .setEncoding(encoding)
            .setSampleRate(Protocol.SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
            .build()
        // No AudioFocus request is made intentionally. This keeps SameVoice in
        // the normal shared mix alongside every other app.
        return AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(format)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(bufferBytes)
            .build()
    }

    private fun startThread(name: String, block: () -> Unit) {
        val thread = Thread(block, name).apply {
            isDaemon = true
            start()
        }
        threads.add(thread)
    }

    // -------------------------------------------------------------- discovery

    private fun discoveryLoop() {
        val socket = discoverySocket ?: return
        while (running) {
            try {
                val buffer = ByteArray(2048)
                val packet = DatagramPacket(buffer, buffer.size)
                socket.receive(packet)
                if (!Discovery.isQuery(packet.data.copyOf(packet.length))) continue
                val offer = Discovery.offer(
                    roomId, hostName, "android", controlPort, mode, sessions.size, false
                )
                socket.send(DatagramPacket(offer, offer.size, packet.address, packet.port))
            } catch (_: SocketException) {
                break
            } catch (_: Exception) {
                if (!running) break
            }
        }
    }

    // -------------------------------------------------------------- tcp control

    private fun acceptLoop() {
        val server = controlServer ?: return
        while (running) {
            try {
                val client = server.accept()
                Thread({ handleClient(client) }, "sv-session").apply {
                    isDaemon = true
                    start()
                }
            } catch (_: SocketException) {
                break
            } catch (_: Exception) {
                if (!running) break
            }
        }
    }

    private fun handleClient(client: Socket) {
        var session: SourceSession? = null
        try {
            if (sessions.size >= 8) {
                client.close()
                return
            }
            client.tcpNoDelay = true
            client.soTimeout = 6000
            val reader = BufferedReader(InputStreamReader(client.getInputStream(), Charsets.UTF_8))
            val writer = BufferedWriter(OutputStreamWriter(client.getOutputStream(), Charsets.UTF_8))
            val helloLine = reader.readLine() ?: run { client.close(); return }
            if (Control.type(helloLine) != "hello") {
                client.close()
                return
            }
            val hello = org.json.JSONObject(helloLine)
            var name = hello.optString("name", "未命名设备").trim()
            if (name.isEmpty()) name = "未命名设备"
            if (name.length > 60) name = name.take(60)
            var platform = hello.optString("platform", "unknown").lowercase()
            if (platform.length > 20) platform = platform.take(20)
            val requestedBits = hello.optInt("bits", 16)
            val bits = min(
                if (requestedBits == 24) 24 else 16,
                if (outputFloat) maxQuality.bits else 16
            )

            val id = nextId.incrementAndGet()
            val token = random.nextLong()
            val remote = client.inetAddress?.hostAddress ?: "?"
            session = SourceSession(id, token, name, platform, remote, bits, client, reader, writer)
            sessions[id] = session
            byToken[token] = session
            mixer.addSource(id)
            client.soTimeout = 0

            session.send(
                Control.welcome(id, token, audioPort, hostName, mode, bits)
            )

            while (running) {
                val line = reader.readLine() ?: break
                if (line.isEmpty()) continue
                session.lastSeenMs = System.currentTimeMillis()
                when (Control.type(line)) {
                    "start" -> {
                        session.sharing = true
                        session.send(Control.simple("started"))
                    }
                    "stop" -> {
                        session.sharing = false
                        mixer.clearSource(id)
                    }
                    "heartbeat" -> {}
                    "renamed" -> {
                        val newName = Control.parseName(line)
                        if (!newName.isNullOrBlank()) session.name = newName.take(60)
                    }
                    "bye" -> return
                }
            }
        } catch (_: Exception) {
            // A source disappearing is normal.
        } finally {
            if (session != null) removeSession(session)
            else try { client.close() } catch (_: Exception) {}
        }
    }

    private fun removeSession(session: SourceSession) {
        if (!session.markClosed()) return
        sessions.remove(session.id)
        byToken.remove(session.token)
        mixer.removeSource(session.id)
        session.close()
    }

    // -------------------------------------------------------------- udp audio

    private fun audioReceiveLoop() {
        val socket = audioSocket ?: return
        while (running) {
            try {
                val buffer = ByteArray(2048)
                val datagram = DatagramPacket(buffer, buffer.size)
                socket.receive(datagram)
                val packet = Packets.decode(datagram.data, datagram.length) ?: continue
                val session = byToken[packet.token] ?: continue
                if (session.id != packet.session) continue
                session.lastSeenMs = System.currentTimeMillis()

                if (packet.type == Protocol.TYPE_PING) {
                    val t2 = Protocol.nowUs()
                    val pong = Packets.encodePong(
                        session.id, session.token, packet.sequence,
                        packet.captureUs, t2, Protocol.nowUs()
                    )
                    synchronized(sendLock) {
                        socket.send(DatagramPacket(pong, pong.size, datagram.address, datagram.port))
                    }
                    continue
                }
                if (packet.type != Protocol.TYPE_AUDIO) continue
                val expectedBytes = session.packetFrames * Protocol.CHANNELS * session.bits / 8
                if (packet.payload.size != expectedBytes) continue

                session.recordPacket(packet.sequence)
                session.bytes.addAndGet(packet.payload.size.toLong())
                if (packet.isMuted) continue

                val samples = FloatArray(session.packetFrames * Protocol.CHANNELS)
                val bb = ByteBuffer.wrap(packet.payload).order(ByteOrder.LITTLE_ENDIAN)
                if (session.bits == 24) {
                    for (i in samples.indices) {
                        var value = (bb.get().toInt() and 0xFF) or
                            ((bb.get().toInt() and 0xFF) shl 8) or
                            ((bb.get().toInt() and 0xFF) shl 16)
                        if (value and 0x800000 != 0) value = value or -0x1000000
                        samples[i] = value / 8_388_608f
                    }
                } else {
                    for (i in samples.indices) samples[i] = bb.short / 32768f
                }
                mixer.push(session.id, packet.captureUs, samples, session.packetFrames)
            } catch (_: SocketException) {
                break
            } catch (_: Exception) {
                if (!running) break
            }
        }
    }

    // -------------------------------------------------------------- output

    private fun outputLoop() {
        val track = audioTrack ?: return
        Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO)
        val buffer = FloatArray(480 * Protocol.CHANNELS)
        val shortBuffer = ShortArray(480 * Protocol.CHANNELS)
        try {
            while (running) {
                mixer.render(buffer, 480)
                if (outputFloat) {
                    var offset = 0
                    while (offset < buffer.size) {
                        val written = track.write(buffer, offset, buffer.size - offset, AudioTrack.WRITE_BLOCKING)
                        if (written <= 0) {
                            lastError = "AudioTrack 写入失败：$written"
                            return
                        }
                        offset += written
                    }
                } else {
                    for (i in buffer.indices) {
                        shortBuffer[i] = (buffer[i] * 32767f).roundToInt().coerceIn(-32768, 32767).toShort()
                    }
                    var offset = 0
                    while (offset < shortBuffer.size) {
                        val written = track.write(shortBuffer, offset, shortBuffer.size - offset, AudioTrack.WRITE_BLOCKING)
                        if (written <= 0) {
                            lastError = "AudioTrack 写入失败：$written"
                            return
                        }
                        offset += written
                    }
                }
            }
        } catch (t: Throwable) {
            if (running) lastError = t.message ?: t.javaClass.simpleName
        }
    }

    private fun maintenanceLoop() {
        while (running) {
            try { Thread.sleep(1000) } catch (_: InterruptedException) { break }
            val now = System.currentTimeMillis()
            for (session in sessions.values) {
                if (now - session.lastSeenMs > 15_000) removeSession(session)
            }
            if (now - lastDiagMs >= 2_000) {
                lastDiagMs = now
                val trim = mixer.trimPercent
                val resyncs = mixer.resyncCount
                for (session in sessions.values) {
                    Log.i(
                        "SameVoiceHost",
                        "src=${session.id} ${session.name} " +
                            "buf=${"%.0f".format(mixer.getBufferAheadMs(session.id))}ms " +
                            "trim=${"%.3f".format(trim)}% resync=$resyncs " +
                            "pkt=${session.packets.get()} miss=${session.missing.get()}"
                    )
                }
            }
        }
    }

    private inner class SourceSession(
        val id: Int,
        val token: Long,
        @Volatile var name: String,
        val platform: String,
        val address: String,
        val bits: Int,
        private val client: Socket,
        private val reader: BufferedReader,
        private val writer: BufferedWriter
    ) {
        val packetFrames: Int = if (bits == 24) Protocol.HIGH_PACKET_FRAMES else Protocol.PACKET_FRAMES

        @Volatile var sharing = false
        @Volatile var lastSeenMs = System.currentTimeMillis()
        val packets = AtomicLong()
        val bytes = AtomicLong()
        val missing = AtomicLong()
        private var lastSequence = 0
        private var hasSequence = false
        private val closed = java.util.concurrent.atomic.AtomicBoolean(false)
        private val writeLock = Any()

        fun recordPacket(sequence: Int) {
            packets.incrementAndGet()
            if (hasSequence) {
                val diff = sequence - lastSequence
                if (diff > 1 && diff < 1000) missing.addAndGet((diff - 1).toLong())
            }
            hasSequence = true
            lastSequence = sequence
        }

        fun send(json: ByteArray) {
            synchronized(writeLock) {
                try {
                    writer.write(String(json, Charsets.UTF_8))
                    writer.write("\n")
                    writer.flush()
                } catch (_: Exception) {
                }
            }
        }

        fun markClosed(): Boolean = closed.compareAndSet(false, true)

        fun close() {
            try { reader.close() } catch (_: Exception) {}
            try { writer.close() } catch (_: Exception) {}
            try { client.close() } catch (_: Exception) {}
        }
    }
}

// ------------------------------------------------------------------ mixer

private class MixEngine(initialMode: HostMode) {
    private class Chunk(
        val startFrame: Long,
        val samples: FloatArray,
        var sampleOffsetFrames: Int,
        var frames: Int
    )

    private class SourceTimeline {
        val chunks = ArrayList<Chunk>(24)
        @Volatile var volume = 0.8f
        var totalFrames = 0L
        var dropped = 0L
        var behindSinceUs = 0L

        @Synchronized
        fun clear() {
            chunks.clear()
            behindSinceUs = 0L
        }

        @Synchronized
        fun push(startFrame: Long, samples: FloatArray, frames: Int) {
            if (frames <= 0) return
            var actualFrames = frames
            if (samples.size < actualFrames * Protocol.CHANNELS) actualFrames = samples.size / Protocol.CHANNELS
            if (actualFrames <= 0) return
            var start = startFrame
            var skip = 0

            if (chunks.isNotEmpty()) {
                val tail = chunks.last()
                val tailStart = tail.startFrame
                val tailEnd = tail.startFrame + tail.frames
                if (start < tailStart) {
                    dropped++
                    return
                }
                if (start < tailEnd) {
                    if (start > tailStart) {
                        val newFrames = (start - tailStart).toInt()
                        if (newFrames <= 0) chunks.removeAt(chunks.size - 1) else tail.frames = newFrames
                    } else {
                        skip = (tailEnd - start).toInt()
                        start = tailEnd
                        if (skip >= actualFrames) {
                            dropped++
                            return
                        }
                    }
                }
                while (chunks.size > 1) {
                    val previous = chunks[chunks.size - 2]
                    if (previous.startFrame + previous.frames > start) chunks.removeAt(chunks.size - 2)
                    else break
                }
            }

            val finalFrames = actualFrames - skip
            if (finalFrames <= 0) {
                dropped++
                return
            }
            chunks.add(Chunk(start, samples, skip, finalFrames))
            totalFrames += finalFrames
            if (chunks.size > 800) {
                val remove = chunks.size - 800
                repeat(remove) { chunks.removeAt(0) }
                dropped += remove
            }
        }

        /**
         * Mixes [blockFrames] output samples read from the timeline starting at
         * [startPos] and advancing by [rate] per output sample. Linear
         * interpolation makes the clock trim a smooth resample instead of
         * dropping/repeating whole frames, which used to be audible as a buzz.
         */
        @Synchronized
        fun mixInto(acc: FloatArray, startPos: Double, rate: Double, blockFrames: Int, gain: Float) {
            if (chunks.isEmpty() || blockFrames <= 0) return
            var remove = 0
            while (remove < chunks.size &&
                chunks[remove].startFrame + chunks[remove].frames <= startPos) remove++
            if (remove > 0) chunks.subList(0, remove).clear()
            if (chunks.isEmpty()) return

            var index = 0
            for (i in 0 until blockFrames) {
                val position = startPos + i * rate
                val frame = floor(position).toLong()
                val fraction = (position - frame.toDouble()).toFloat()
                while (index < chunks.size &&
                    chunks[index].startFrame + chunks[index].frames <= frame) index++
                if (index >= chunks.size) break
                val chunk = chunks[index]
                if (frame < chunk.startFrame) continue // packet gap: keep silence

                val offset = (frame - chunk.startFrame).toInt()
                val source = (offset + chunk.sampleOffsetFrames) * Protocol.CHANNELS
                if (source < 0 || source + 1 >= chunk.samples.size) continue
                val left = chunk.samples[source]
                val right = chunk.samples[source + 1]
                var nextLeft = left
                var nextRight = right
                if (offset + 1 < chunk.frames && source + 3 < chunk.samples.size) {
                    nextLeft = chunk.samples[source + 2]
                    nextRight = chunk.samples[source + 3]
                } else if (offset + 1 >= chunk.frames && index + 1 < chunks.size) {
                    // Continue into the next packet so the packet boundary is
                    // interpolated as well; otherwise every 2.5/5 ms boundary
                    // adds a small step and becomes a faint buzz.
                    val next = chunks[index + 1]
                    if (next.startFrame == chunk.startFrame + chunk.frames &&
                        next.samples.size >= next.sampleOffsetFrames * Protocol.CHANNELS + 2
                    ) {
                        val base = next.sampleOffsetFrames * Protocol.CHANNELS
                        nextLeft = next.samples[base]
                        nextRight = next.samples[base + 1]
                    }
                }

                val dest = i * Protocol.CHANNELS
                if (dest + 1 >= acc.size) break
                acc[dest] += (left + (nextLeft - left) * fraction) * gain
                acc[dest + 1] += (right + (nextRight - right) * fraction) * gain
            }
        }

        @Synchronized
        fun bufferAheadMs(position: Double): Double {
            if (chunks.isEmpty()) return 0.0
            val newestEnd = chunks.last().startFrame + chunks.last().frames
            return max(0.0, newestEnd.toDouble() - position) * 1000.0 / Protocol.SAMPLE_RATE
        }

        @Synchronized
        fun hasDataAfter(frame: Long): Boolean =
            chunks.isNotEmpty() && chunks.last().startFrame + chunks.last().frames > frame
    }

    private val renderLock = Any()
    private val sourcesLock = Any()
    private val sources = HashMap<Int, SourceTimeline>()
    private var acc = FloatArray(Protocol.SAMPLE_RATE * 2)
    private var startUs = 0L
    private var renderedFrames = 0L
    private var mixPosition = 0.0
    private var targetMs = initialMode.bufferMs
    private var running = false

    // Clock-trim state. The phone's playback device and the source device never
    // run at exactly 48 kHz, and the source timestamp rate is derived from its
    // own capture clock. A fixed +/-2 frame nudge per block could only absorb
    // 0.4 % of that difference; anything larger drained the queue to zero.
    // The trim lets the mix cursor run at 1 +/- 3 % of the AudioTrack rate, and
    // the mixer reads the timeline at that fractional rate (linear
    // interpolation), so the correction is inaudible instead of clicking.
    private var trim = 0.0
    private var smoothErrorMs = 0.0
    @Volatile private var writtenProgressUs = 0L
    private var emptySinceUs = 0L
    private var lastResyncUs = 0L
    private var resyncs = 0L

    val trimPercent: Double get() = synchronized(renderLock) { trim * 100.0 }
    val resyncCount: Long get() = synchronized(renderLock) { resyncs }

    @Volatile var masterVolume = 0.8f
    @Volatile var peak = 0.0
        private set

    fun start(outputLatencyUs: Long = 0) {
        synchronized(renderLock) {
            // AudioTrack's write cursor is ahead of the actually audible
            // sample by its buffer. Anchor the mix timeline to the playback
            // cursor, not the write cursor, otherwise the displayed network
            // buffer is short by the AudioTrack buffer (and worse when another
            // app changes the output route/buffer).
            startUs = Protocol.nowUs() - outputLatencyUs
            renderedFrames = 0
            mixPosition = 0.0
            trim = 0.0
            smoothErrorMs = 0.0
            emptySinceUs = 0L
            lastResyncUs = 0L
            resyncs = 0L
            writtenProgressUs = Protocol.nowUs()
            running = true
        }
    }

    fun stop() {
        synchronized(renderLock) { running = false }
    }

    fun setMode(mode: HostMode) {
        synchronized(renderLock) { targetMs = mode.bufferMs }
        clearBuffers()
    }

    fun setBufferMs(ms: Int) {
        synchronized(renderLock) { targetMs = ms.coerceIn(40, 1000) }
        clearBuffers()
    }

    fun addSource(id: Int) {
        synchronized(sourcesLock) { sources[id] = SourceTimeline() }
    }

    fun removeSource(id: Int) {
        synchronized(sourcesLock) { sources.remove(id) }
    }

    fun clearSource(id: Int) {
        synchronized(sourcesLock) { sources[id]?.clear() }
    }

    fun clearBuffers() {
        synchronized(sourcesLock) { sources.values.forEach { it.clear() } }
    }

    fun getBufferAheadMs(id: Int): Double {
        val timeline = synchronized(sourcesLock) { sources[id] } ?: return 0.0
        val position = synchronized(renderLock) { mixPosition }
        return timeline.bufferAheadMs(position)
    }

    fun setSourceVolume(id: Int, volume: Float) {
        synchronized(sourcesLock) {
            sources[id]?.volume = volume.coerceIn(0f, 1f)
        }
    }

    fun getSourceVolume(id: Int): Float =
        synchronized(sourcesLock) { sources[id]?.volume ?: 0.8f }

    fun push(id: Int, captureHostUs: Long, samples: FloatArray, frames: Int) {
        val timeline = synchronized(sourcesLock) { sources[id] } ?: return
        var startFrame = 0L
        var rendered = 0L
        var resync = false
        var reason = ""
        synchronized(renderLock) {
            if (!running) return
            val target = targetMs
            val nowUs = Protocol.nowUs()
            rendered = renderedFrames
            startFrame = ((captureHostUs + target * 1000L - startUs) *
                Protocol.SAMPLE_RATE / 1_000_000.0).roundToLongSafe()

            // The blocking AudioTrack write stalled, so the mix cursor no
            // longer follows wall-clock time.
            val stalled = nowUs - writtenProgressUs > 500_000L

            // A packet that lands behind the cursor and *stays* behind means
            // the mapping is broken (source clock faster than the trim can
            // absorb, or a timestamp step into the past). A short burst of
            // late packets after a network gap resets the streak as soon as a
            // current packet arrives, so it does not trigger this.
            if (startFrame + frames <= rendered) {
                if (timeline.behindSinceUs == 0L) timeline.behindSinceUs = nowUs
            } else {
                timeline.behindSinceUs = 0L
            }
            val deadlocked = timeline.behindSinceUs != 0L &&
                nowUs - timeline.behindSinceUs > 200_000L
            val drained = emptySinceUs != 0L && nowUs - emptySinceUs > 300_000L

            if ((stalled || (deadlocked && drained)) &&
                nowUs - lastResyncUs > 1_000_000L) {
                lastResyncUs = nowUs
                // Put the current packet exactly `target` ahead of the cursor:
                // startFrame = (capture + target - startUs) must equal
                // rendered + target, so startUs = capture - rendered.
                startUs = captureHostUs - rendered * 1_000_000L / Protocol.SAMPLE_RATE
                startFrame = ((captureHostUs + target * 1000L - startUs) *
                    Protocol.SAMPLE_RATE / 1_000_000.0).roundToLongSafe()
                timeline.behindSinceUs = 0L
                resync = true
                reason = if (stalled) "stalled" else "drained"
                resyncs++
            }
        }
        if (resync) {
            Log.w("SameVoiceHost", "resync reason=$reason rendered=$rendered startFrame=$startFrame")
            clearBuffers()
        }

        if (startFrame + frames <= rendered - Protocol.SAMPLE_RATE / 4) return
        if (startFrame > rendered + Protocol.SAMPLE_RATE * 10L) return
        timeline.push(startFrame, samples, frames)
    }

    fun render(destination: FloatArray, frames: Int) {
        synchronized(renderLock) {
            java.util.Arrays.fill(destination, 0f)
            if (!running) {
                peak = 0.0
                return
            }
            val needed = frames * Protocol.CHANNELS
            if (acc.size < needed) acc = FloatArray(needed)
            acc.fill(0f, 0, needed)

            val timelines = synchronized(sourcesLock) { sources.values.toList() }
            val activeTimelines = timelines.filter { it.hasDataAfter(renderedFrames) }
            val active = activeTimelines.size
            val normalization = if (active > 1) 1f / active else 1f

            // Clock drift between the phone's playback clock and the source's
            // capture clock slowly drains or grows the jitter buffer. The mix
            // position advances at 1 +/- 3 % and the mixer resamples the
            // timeline read at that rate, so the correction is smooth (no
            // dropped/repeated frames, no buzz) instead of a fixed +/-2 frame
            // nudge. Two-stage smoothing keeps packet-sized (2.5/5 ms)
            // timestamp steps from turning into pitch wobble.
            val nowUs = Protocol.nowUs()
            if (active > 0) {
                emptySinceUs = 0L
            } else if (emptySinceUs == 0L) {
                emptySinceUs = nowUs
            }
            val errorMs = if (active > 0)
                activeTimelines.minOf { it.bufferAheadMs(mixPosition) } - targetMs
            else
                -targetMs.toDouble()
            smoothErrorMs += (errorMs - smoothErrorMs) * 0.08
            val wanted = (smoothErrorMs * 0.0004).coerceIn(-0.03, 0.03)
            trim += (wanted - trim) * 0.05
            val rate = 1.0 + trim

            for (timeline in timelines) {
                timeline.mixInto(acc, mixPosition, rate, frames, timeline.volume * normalization)
            }

            var maxPeak = 0.0
            for (i in 0 until needed) {
                val sample = acc[i] * masterVolume
                maxPeak = max(maxPeak, abs(sample).toDouble())
                destination[i] = softLimit(sample)
            }
            peak = maxPeak
            mixPosition += frames * rate
            renderedFrames = mixPosition.toLong()
            writtenProgressUs = Protocol.nowUs()
        }
    }

    private fun softLimit(x: Float): Float {
        val knee = 0.90f
        val a = abs(x)
        if (a <= knee) return x
        val over = (a - knee) / (1f - knee)
        val limited = knee + (1f - knee) * tanh(over)
        return if (x < 0) -limited else limited
    }

    private fun Double.roundToLongSafe(): Long = when {
        this >= Long.MAX_VALUE.toDouble() -> Long.MAX_VALUE
        this <= Long.MIN_VALUE.toDouble() -> Long.MIN_VALUE
        else -> kotlin.math.round(this).toLong()
    }
}
