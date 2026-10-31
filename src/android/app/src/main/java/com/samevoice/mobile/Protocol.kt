package com.samevoice.mobile

import android.os.SystemClock
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.ByteOrder

object Protocol {
    const val VERSION = 1
    const val SAMPLE_RATE = 48_000
    const val CHANNELS = 2
    const val BITS = 16
    const val HIGH_BITS = 24
    const val BYTES_PER_FRAME = 4
    const val HIGH_BYTES_PER_FRAME = 6
    const val PACKET_FRAMES = 240
    const val PACKET_BYTES = 960
    const val HIGH_PACKET_FRAMES = 120
    const val HIGH_PACKET_BYTES = 720
    const val PACKET_MS = 5
    const val CONTROL_PORT = 47820
    const val DISCOVERY_PORT = 47821
    const val AUDIO_PORT = 47822
    const val HEADER_SIZE = 36
    const val TYPE_AUDIO = 1
    const val TYPE_PING = 2
    const val TYPE_PONG = 3
    const val MAGIC = "SVA1"

    fun nowUs(): Long = SystemClock.elapsedRealtimeNanos() / 1_000L
}

enum class HostMode(
    val wire: String,
    val bufferMs: Int,
    val label: String,
    val subtitle: String
) {
    BALANCED("balanced", 240, "同步优先", "约 240 ms 缓冲，多台设备严格对齐"),
    LOW("low", 120, "最低延迟", "约 120 ms 缓冲，优先速度，适合稳定 Wi-Fi");

    companion object {
        fun fromWire(value: String?): HostMode =
            if (value.equals("low", ignoreCase = true)) LOW else BALANCED
    }
}

data class HostOffer(
    val roomId: String,
    val name: String,
    val platform: String,
    val address: String,
    val tcpPort: Int,
    val mode: HostMode,
    val sources: Int,
    val pinRequired: Boolean
)

enum class AudioQuality(val bits: Int, val label: String) {
    STANDARD(16, "标准 48k/16-bit"),
    HIGH(24, "高音质 48k/24-bit");

    val bytesPerFrame: Int get() = Protocol.CHANNELS * bits / 8
    val packetFrames: Int get() = if (this == HIGH) Protocol.HIGH_PACKET_FRAMES else Protocol.PACKET_FRAMES
    val packetBytes: Int get() = packetFrames * bytesPerFrame

    companion object {
        fun fromBits(value: Int?): AudioQuality = if (value == 24) HIGH else STANDARD
    }
}

data class Welcome(
    val session: Int,
    val token: Long,
    val audioPort: Int,
    val hostName: String,
    val mode: HostMode,
    val bits: Int = 16
)

data class AudioPacket(
    val type: Int,
    val flags: Int,
    val session: Int,
    val token: Long,
    val sequence: Int,
    val captureUs: Long,
    val payload: ByteArray
) {
    val isMuted: Boolean get() = (flags and 1) != 0
}

object Packets {
    fun encode(
        type: Int,
        session: Int,
        token: Long,
        sequence: Int,
        captureUs: Long,
        payload: ByteArray,
        flags: Int = 0
    ): ByteArray {
        val buffer = ByteBuffer.allocate(Protocol.HEADER_SIZE + payload.size).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put('S'.code.toByte())
        buffer.put('V'.code.toByte())
        buffer.put('A'.code.toByte())
        buffer.put('1'.code.toByte())
        buffer.put(Protocol.VERSION.toByte())
        buffer.put(type.toByte())
        buffer.put(flags.toByte())
        buffer.put(0)
        buffer.putInt(session)
        buffer.putLong(token)
        buffer.putInt(sequence)
        buffer.putLong(captureUs)
        buffer.putShort(payload.size.toShort())
        buffer.putShort(0)
        buffer.put(payload)
        return buffer.array()
    }

    fun decode(data: ByteArray, length: Int = data.size): AudioPacket? {
        if (length < Protocol.HEADER_SIZE) return null
        if (data[0] != 'S'.code.toByte() || data[1] != 'V'.code.toByte() ||
            data[2] != 'A'.code.toByte() || data[3] != '1'.code.toByte()
        ) return null
        if (data[4].toInt() and 0xFF != Protocol.VERSION) return null
        val buffer = ByteBuffer.wrap(data, 0, length).order(ByteOrder.LITTLE_ENDIAN)
        buffer.position(5)
        val type = buffer.get().toInt() and 0xFF
        val flags = buffer.get().toInt() and 0xFF
        buffer.get()
        val session = buffer.int
        val token = buffer.long
        val sequence = buffer.int
        val captureUs = buffer.long
        val payloadLength = buffer.short.toInt() and 0xFFFF
        buffer.short
        if (payloadLength > length - Protocol.HEADER_SIZE) return null
        val payload = ByteArray(payloadLength)
        System.arraycopy(data, Protocol.HEADER_SIZE, payload, 0, payloadLength)
        return AudioPacket(type, flags, session, token, sequence, captureUs, payload)
    }

    fun encodePong(session: Int, token: Long, requestSequence: Int, t1: Long, t2: Long, t3: Long): ByteArray {
        val payload = ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN)
            .putLong(t1).putLong(t2).array()
        return encode(Protocol.TYPE_PONG, session, token, requestSequence, t3, payload)
    }

    fun pongTimes(packet: AudioPacket): Pair<Long, Long>? {
        if (packet.payload.size < 16) return null
        val b = ByteBuffer.wrap(packet.payload).order(ByteOrder.LITTLE_ENDIAN)
        return Pair(b.long, b.long)
    }
}

object Discovery {
    fun query(): ByteArray = JSONObject().apply {
        put("type", "query")
        put("proto", Protocol.VERSION)
    }.toString().toByteArray(Charsets.UTF_8)

    fun isQuery(data: ByteArray): Boolean = try {
        val json = JSONObject(String(data, Charsets.UTF_8))
        json.optString("type") == "query" && json.optInt("proto", Protocol.VERSION) == Protocol.VERSION
    } catch (_: Exception) {
        false
    }

    fun offer(
        roomId: String,
        name: String,
        platform: String,
        tcpPort: Int,
        mode: HostMode,
        sources: Int,
        pinRequired: Boolean
    ): ByteArray = JSONObject().apply {
        put("type", "offer")
        put("proto", Protocol.VERSION)
        put("roomId", roomId)
        put("name", name)
        put("platform", platform)
        put("tcpPort", tcpPort)
        put("mode", mode.wire)
        put("sources", sources)
        put("pinRequired", pinRequired)
    }.toString().toByteArray(Charsets.UTF_8)

    fun parseOffer(data: ByteArray, remoteAddress: String): HostOffer? = try {
        val json = JSONObject(String(data, Charsets.UTF_8))
        if (json.optString("type") != "offer" || json.optInt("proto") != Protocol.VERSION) null
        else HostOffer(
            roomId = json.optString("roomId"),
            name = json.optString("name", "SameVoice"),
            platform = json.optString("platform"),
            address = remoteAddress,
            tcpPort = json.optInt("tcpPort", Protocol.CONTROL_PORT),
            mode = HostMode.fromWire(json.optString("mode")),
            sources = json.optInt("sources"),
            pinRequired = json.optBoolean("pinRequired")
        )
    } catch (_: Exception) {
        null
    }
}

object Control {
    fun hello(name: String, platform: String, pin: String = "", bits: Int = 16): ByteArray = JSONObject().apply {
        put("type", "hello")
        put("proto", Protocol.VERSION)
        put("name", name)
        put("platform", platform)
        put("pin", pin)
        put("bits", bits)
    }.toString().toByteArray(Charsets.UTF_8)

    fun welcome(
        session: Int,
        token: Long,
        audioPort: Int,
        hostName: String,
        mode: HostMode,
        bits: Int = 16
    ): ByteArray = JSONObject().apply {
        put("type", "welcome")
        put("proto", Protocol.VERSION)
        put("session", session)
        put("token", java.lang.Long.toUnsignedString(token, 16).padStart(16, '0'))
        put("audioPort", audioPort)
        put("name", hostName)
        put("mode", mode.wire)
        put("sampleRate", Protocol.SAMPLE_RATE)
        put("bits", bits)
    }.toString().toByteArray(Charsets.UTF_8)

    fun simple(type: String): ByteArray = JSONObject().put("type", type).toString().toByteArray(Charsets.UTF_8)
    fun mode(mode: HostMode): ByteArray = JSONObject().apply {
        put("type", "mode")
        put("mode", mode.wire)
    }.toString().toByteArray(Charsets.UTF_8)

    fun error(message: String): ByteArray = JSONObject().apply {
        put("type", "error")
        put("message", message)
    }.toString().toByteArray(Charsets.UTF_8)

    fun type(line: String): String? = try { JSONObject(line).optString("type") } catch (_: Exception) { null }

    fun parseWelcome(line: String): Welcome? = try {
        val json = JSONObject(line)
        if (json.optString("type") != "welcome" || json.optInt("proto") != Protocol.VERSION) null
        else {
            val token = java.lang.Long.parseUnsignedLong(json.getString("token"), 16)
            val port = json.getInt("audioPort")
            if (port <= 0 || port > 65535) null
            else {
                val bits = json.optInt("bits", 16)
                if (bits != 16 && bits != 24) null
                else Welcome(
                    session = json.getInt("session"),
                    token = token,
                    audioPort = port,
                    hostName = json.optString("name", "SameVoice"),
                    mode = HostMode.fromWire(json.optString("mode")),
                    bits = bits
                )
            }
        }
    } catch (_: Exception) {
        null
    }

    fun parseMode(line: String): HostMode? = try {
        val json = JSONObject(line)
        if (json.optString("type") == "mode") HostMode.fromWire(json.optString("mode")) else null
    } catch (_: Exception) {
        null
    }

    fun parseError(line: String): String? = try {
        val json = JSONObject(line)
        if (json.optString("type") == "error") json.optString("message", "输出端返回错误") else null
    } catch (_: Exception) {
        null
    }

    fun parseName(line: String): String? = try {
        val json = JSONObject(line)
        if (json.optString("type") == "renamed") json.optString("name") else null
    } catch (_: Exception) {
        null
    }
}
