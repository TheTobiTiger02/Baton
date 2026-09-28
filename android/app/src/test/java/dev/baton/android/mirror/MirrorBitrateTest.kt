package dev.baton.android.mirror

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class MirrorBitrateTest {
    @Test
    fun stepsDownAtOnceAndUpSlowly() {
        var now = 0L
        val ladder = MirrorBitrate { now }
        assertEquals(10_000_000, ladder.bitrate)

        now = 1_000
        assertNull(ladder.dropped())
        now = 3_000
        assertEquals(5_000_000, ladder.dropped())
        now = 5_000
        assertEquals(2_500_000, ladder.dropped())

        now = 10_000
        assertNull(ladder.tick())
        now = 15_000
        assertEquals(5_000_000, ladder.tick())
        now = 25_000
        assertEquals(10_000_000, ladder.tick())
        now = 60_000
        assertNull(ladder.tick())
    }
}
