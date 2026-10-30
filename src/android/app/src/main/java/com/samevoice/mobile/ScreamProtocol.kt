package com.samevoice.mobile

import kotlin.math.max

/**
 * Scream virtual sound card wire format.
 *
 * 5-byte header:
 *   [0] sample-rate marker: bit7 0 = 48k base, 1 = 44.1k base; low 7 bits = multiplier
 *   [1] bits per sample (16, 24 or 32)
 *   [2] channel count
 *   [3..4] little-endian Windows speaker mask
 * followed by interleaved little-endian PCM.
 */
data class ScreamFormat(
    val sampleRate: Int,
    val bits: Int,
    val channels: Int,
    val channelMask: Int
) {
    val frameBytes: Int get() = channels * (bits / 8)
    val description: String get() = "$sampleRate Hz · $bits-bit · $channels ch"
}

object ScreamProtocol {
    const val GROUP_ADDRESS = "239.255.77.77"
    const val PORT = 4010
    const val HEADER_SIZE = 5
    const val MAX_PACKET = 1600

    fun parseFormat(header: ByteArray, length: Int): ScreamFormat? {
        if (length < HEADER_SIZE) return null
        val marker = header[0].toInt() and 0xFF
        val multiplier = marker and 0x7F
        if (multiplier == 0) return null
        val base = if (marker and 0x80 != 0) 44_100 else 48_000
        val sampleRate = base * multiplier
        if (sampleRate !in 8_000..192_000) return null

        val bits = header[1].toInt() and 0xFF
        if (bits != 16 && bits != 24 && bits != 32) return null
        val channels = header[2].toInt() and 0xFF
        if (channels !in 1..8) return null
        val mask = (header[3].toInt() and 0xFF) or ((header[4].toInt() and 0xFF) shl 8)
        return ScreamFormat(sampleRate, bits, channels, mask)
    }

    /**
     * Convert one Scream packet payload to stereo 16-bit PCM. Scream's default
     * setup is 48 kHz/16-bit/stereo; the driver can also emit 24/32-bit and
     * multichannel, which we downmix to the first stereo pair.
     */
    fun toStereo16(payload: ByteArray, length: Int, format: ScreamFormat): ShortArray? {
        if (length < HEADER_SIZE + format.frameBytes) return null
        val bytesPerSample = format.bits / 8
        val frameBytes = format.frameBytes
        val frames = (length - HEADER_SIZE) / frameBytes
        if (frames <= 0) return null
        val output = ShortArray(frames * 2)
        var offset = HEADER_SIZE
        for (frame in 0 until frames) {
            var left = 0
            var right = 0
            for (channel in 0 until format.channels) {
                val sample = readSample(payload, offset, bytesPerSample, format.bits)
                offset += bytesPerSample
                if (channel == 0) left = sample
                if (channel == 1 || format.channels == 1) right = sample
            }
            output[frame * 2] = left.toShort()
            output[frame * 2 + 1] = right.toShort()
        }
        return output
    }

    private fun readSample(data: ByteArray, offset: Int, bytesPerSample: Int, bits: Int): Int {
        if (offset + bytesPerSample > data.size) return 0
        return when (bits) {
            16 -> (data[offset].toInt() and 0xFF) or ((data[offset + 1].toInt() and 0xFF) shl 8)
            24 -> {
                var value = (data[offset].toInt() and 0xFF) or
                    ((data[offset + 1].toInt() and 0xFF) shl 8) or
                    ((data[offset + 2].toInt() and 0xFF) shl 16)
                if (value and 0x800000 != 0) value = value or -0x1000000
                value shr 8
            }
            else -> {
                val value = (data[offset].toInt() and 0xFF) or
                    ((data[offset + 1].toInt() and 0xFF) shl 8) or
                    ((data[offset + 2].toInt() and 0xFF) shl 16) or
                    ((data[offset + 3].toInt() and 0xFF) shl 24)
                value shr 16
            }
        }
    }

    fun peakLevel(samples: ShortArray): Double {
        if (samples.isEmpty()) return 0.0
        var peak = 0
        for (sample in samples) peak = max(peak, kotlin.math.abs(sample.toInt()))
        return peak / 32768.0
    }
}
