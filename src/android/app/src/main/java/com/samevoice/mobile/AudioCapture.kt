package com.samevoice.mobile

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.os.Process
import kotlin.math.max

interface PcmCapture {
    fun start(onData: (ShortArray, Long) -> Unit, onError: (String) -> Unit)
    fun stop()
}

/**
 * Multi-output source support: one AudioRecord feeds several SourceEngine
 * sessions. The first open starts the underlying capture, and the last close
 * stops it.
 */
class SharedPcmCapture(private val underlying: PcmCapture) {
    private data class Sink(
        val onData: (ShortArray, Long) -> Unit,
        val onError: (String) -> Unit
    )

    private val gate = Any()
    private val sinks = LinkedHashMap<Int, Sink>()
    private var nextId = 1
    private var started = false

    fun open(onData: (ShortArray, Long) -> Unit, onError: (String) -> Unit): Int {
        synchronized(gate) {
            val id = nextId++
            sinks[id] = Sink(onData, onError)
            if (!started) {
                try {
                    underlying.start(::dispatchData, ::dispatchError)
                    started = true
                } catch (t: Throwable) {
                    sinks.remove(id)
                    throw t
                }
            }
            return id
        }
    }

    fun close(id: Int) {
        synchronized(gate) {
            sinks.remove(id)
            if (started && sinks.isEmpty()) {
                started = false
                try { underlying.stop() } catch (_: Exception) {}
            }
        }
    }

    fun stopAll() {
        synchronized(gate) {
            sinks.clear()
            if (started) {
                started = false
                try { underlying.stop() } catch (_: Exception) {}
            }
        }
    }

    private fun dispatchData(samples: ShortArray, timestampUs: Long) {
        val snapshot = synchronized(gate) { sinks.values.toList() }
        for (sink in snapshot) {
            try { sink.onData(samples, timestampUs) } catch (_: Throwable) {}
        }
    }

    private fun dispatchError(message: String) {
        val snapshot = synchronized(gate) { sinks.values.toList() }
        for (sink in snapshot) {
            try { sink.onError(message) } catch (_: Throwable) {}
        }
    }
}

/**
 * Captures internal app audio with MediaProjection + AudioPlaybackCapture.
 * Calls are made on a dedicated audio-priority thread.
 */
class MediaProjectionAudioCapture(private val projection: MediaProjection) : PcmCapture {
    @Volatile private var running = false
    private var record: AudioRecord? = null
    private var thread: Thread? = null

    override fun start(onData: (ShortArray, Long) -> Unit, onError: (String) -> Unit) {
        if (running) return
        val captureConfig = AudioPlaybackCaptureConfiguration.Builder(projection)
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
            .build()
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(Protocol.SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()
        val min = AudioRecord.getMinBufferSize(
            Protocol.SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_STEREO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (min <= 0) throw IllegalStateException("设备不支持 48 kHz 立体声采集")
        val bufferBytes = max(min, Protocol.PACKET_BYTES * 16) // 80 ms capture cushion

        val audioRecord = AudioRecord.Builder()
            .setAudioPlaybackCaptureConfig(captureConfig)
            .setAudioFormat(format)
            .setBufferSizeInBytes(bufferBytes)
            .build()
        if (audioRecord.state != AudioRecord.STATE_INITIALIZED) {
            try { audioRecord.release() } catch (_: Exception) {}
            throw IllegalStateException("无法初始化内部音频采集（可能被系统或版权策略禁止）")
        }
        if (audioRecord.sampleRate != Protocol.SAMPLE_RATE || audioRecord.channelCount != Protocol.CHANNELS) {
            val actualRate = audioRecord.sampleRate
            val actualChannels = audioRecord.channelCount
            try { audioRecord.release() } catch (_: Exception) {}
            throw IllegalStateException(
                "内部音频格式不被支持：实际 ${actualRate} Hz/${actualChannels} 声道，需要 48000 Hz/2 声道"
            )
        }
        record = audioRecord
        running = true
        try {
            audioRecord.startRecording()
        } catch (t: Throwable) {
            running = false
            try { audioRecord.release() } catch (_: Exception) {}
            record = null
            throw t
        }
        if (audioRecord.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            running = false
            try { audioRecord.release() } catch (_: Exception) {}
            record = null
            throw IllegalStateException("内部音频采集未能启动")
        }

        thread = Thread({
            try {
                Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            } catch (_: Throwable) {
                Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO)
            }
            val buffer = ShortArray(960 * Protocol.CHANNELS) // 20 ms
            try {
                while (running) {
                    val read = audioRecord.read(buffer, 0, buffer.size, AudioRecord.READ_BLOCKING)
                    if (read <= 0) {
                        if (read < 0 && running) {
                            onError("AudioRecord 读取失败：$read")
                            break
                        }
                        continue
                    }
                    val frames = read / Protocol.CHANNELS
                    if (frames <= 0) continue
                    val durationUs = frames * 1_000_000L / Protocol.SAMPLE_RATE
                    val timestamp = Protocol.nowUs() - durationUs
                    val copy = buffer.copyOf(read)
                    onData(copy, timestamp)
                }
            } catch (t: Throwable) {
                if (running) onError(t.message ?: t.javaClass.simpleName)
            }
        }, "sv-audio-capture").apply {
            priority = Thread.MAX_PRIORITY
            isDaemon = true
            start()
        }
    }

    override fun stop() {
        running = false
        try { record?.stop() } catch (_: Exception) {}
        if (thread != Thread.currentThread()) {
            try { thread?.join(500) } catch (_: InterruptedException) {}
        }
        thread = null
        try { record?.release() } catch (_: Exception) {}
        record = null
    }
}
