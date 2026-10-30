package com.samevoice.mobile

import java.util.ArrayDeque

/**
 * NTP-style offset estimator. All values are microseconds on each device's
 * monotonic clock. Offset = hostClock - localClock.
 */
class ClockSync {
    private data class Sample(val offsetUs: Long, val rttUs: Long)

    private val gate = Any()
    private val samples = ArrayDeque<Sample>()
    private var currentOffsetUs = 0L
    private var initialized = false
    private var lastAdjustUs = 0L
    private var count = 0

    val offsetUs: Long get() = synchronized(gate) { currentOffsetUs }
    val isReady: Boolean get() = synchronized(gate) { initialized }
    val sampleCount: Int get() = synchronized(gate) { count }
    val bestRttUs: Long get() = synchronized(gate) { samples.minOfOrNull { it.rttUs } ?: -1L }

    fun addSample(t1Local: Long, t2Host: Long, t3Host: Long, t4Local: Long) {
        var rtt = (t4Local - t1Local) - (t3Host - t2Host)
        if (rtt < 0) rtt = 0
        val offset = ((t2Host - t1Local) + (t3Host - t4Local)) / 2

        synchronized(gate) {
            samples.addLast(Sample(offset, rtt))
            while (samples.size > 8) samples.removeFirst()
            count++
            val best = samples.minByOrNull { it.rttUs } ?: return
            val now = Protocol.nowUs()
            if (!initialized) {
                // Wait for a few samples before declaring the clock usable.
                if (count < 3) return
                currentOffsetUs = best.offsetUs
                initialized = true
                lastAdjustUs = now
                return
            }
            val elapsed = (now - lastAdjustUs).coerceAtLeast(0)
            val maxStep = maxOf(200L, elapsed * 2 / 1000)
            var delta = best.offsetUs - currentOffsetUs
            if (delta > maxStep) delta = maxStep
            else if (delta < -maxStep) delta = -maxStep
            currentOffsetUs += delta
            lastAdjustUs = now
        }
    }

    fun toHost(localUs: Long): Long = localUs + offsetUs
}
