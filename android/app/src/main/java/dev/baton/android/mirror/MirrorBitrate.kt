package dev.baton.android.mirror

import android.os.SystemClock

/**
 * The mirror's bitrate, stepped down the moment the link can't keep up and back up slowly once
 * it has kept up for a while. The twin of the PC's `BitrateLadder`, which does the same for
 * windows streamed to the phone.
 */
internal class MirrorBitrate(private val clock: () -> Long = SystemClock::elapsedRealtime) {
    private var step = 0
    private var changedAt = clock()
    private var droppedAt = clock()

    val bitrate: Int get() = STEPS[step]

    /** Frames had to be dropped. Returns the new bitrate when it went down. */
    fun dropped(): Int? {
        val now = clock()
        droppedAt = now
        if (step >= STEPS.lastIndex || now - changedAt < DOWN_SPACING_MS) return null
        step++
        changedAt = now
        return bitrate
    }

    /** Called regularly. Returns the new bitrate when the link earned a step up. */
    fun tick(): Int? {
        val now = clock()
        if (step == 0 || now - droppedAt < UP_AFTER_MS || now - changedAt < UP_AFTER_MS) return null
        step--
        changedAt = now
        return bitrate
    }

    companion object {
        val STEPS = intArrayOf(10_000_000, 5_000_000, 2_500_000, 1_200_000)
        const val DOWN_SPACING_MS = 2_000L
        const val UP_AFTER_MS = 10_000L
    }
}
