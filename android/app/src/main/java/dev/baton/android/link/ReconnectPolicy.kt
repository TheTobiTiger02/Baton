package dev.baton.android.link

import kotlin.random.Random

object ReconnectPolicy {
    private val ceilingsMs = longArrayOf(1_000L, 2_000L, 5_000L, 10_000L, 30_000L)

    fun ceilingMs(attempt: Int): Long = ceilingsMs[attempt.coerceIn(0, ceilingsMs.lastIndex)]

    fun fullJitterDelayMs(attempt: Int, random: Random = Random.Default): Long =
        random.nextLong(250L, ceilingMs(attempt) + 1L)

    val maxAttemptIndex: Int get() = ceilingsMs.lastIndex
}
