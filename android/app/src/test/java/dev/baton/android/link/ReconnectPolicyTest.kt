package dev.baton.android.link

import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ReconnectPolicyTest {
    @Test
    fun backoffCeilingIsBounded() {
        assertEquals(1_000L, ReconnectPolicy.ceilingMs(0))
        assertEquals(1_000L, ReconnectPolicy.ceilingMs(-1))
        assertEquals(30_000L, ReconnectPolicy.ceilingMs(99))
    }

    @Test
    fun fullJitterStaysInsideAttemptWindow() {
        val random = Random(42)
        repeat(100) {
            val delay = ReconnectPolicy.fullJitterDelayMs(3, random)
            assertTrue(delay in 250L..10_000L)
        }
    }
}
