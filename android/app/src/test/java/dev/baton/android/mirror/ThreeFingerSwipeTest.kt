package dev.baton.android.mirror

import dev.baton.android.handoff.Swipes
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.io.ByteArrayInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

class ThreeFingerSwipeTest {
    private val screen = Touchscreen("/dev/input/event2", 1000, 2000)

    /**
     * The touchscreen's bytes (64-bit input_event structs) for [fingers] moving together from y
     * [from] to [to] in [steps] frames, [seconds] apart.
     */
    private fun touch(fingers: Int, from: Int, to: Int, dx: Int = 0, steps: Int = 5, seconds: Double = 0.02): ByteArray {
        var time = 100.0
        val out = ByteBuffer.allocate(24 * 1000).order(ByteOrder.nativeOrder())
        fun event(type: Int, code: Int, value: Int) {
            out.putLong(time.toLong()).putLong(((time % 1) * 1_000_000).toLong()).putShort(type.toShort()).putShort(code.toShort()).putInt(value)
        }
        fun abs(code: Int, value: Int) = event(ThreeFingerSwipe.EV_ABS, code, value)
        fun sync() {
            event(ThreeFingerSwipe.EV_SYN, ThreeFingerSwipe.SYN_REPORT, 0)
            time += seconds
        }
        for (slot in 0 until fingers) {
            abs(ThreeFingerSwipe.ABS_MT_SLOT, slot)
            abs(ThreeFingerSwipe.ABS_MT_TRACKING_ID, 10 + slot)
            abs(ThreeFingerSwipe.ABS_MT_POSITION_X, 300 + slot * 150)
            abs(ThreeFingerSwipe.ABS_MT_POSITION_Y, from)
            sync()
        }
        for (step in 1..steps) {
            for (slot in 0 until fingers) {
                abs(ThreeFingerSwipe.ABS_MT_SLOT, slot)
                abs(ThreeFingerSwipe.ABS_MT_POSITION_X, 300 + slot * 150 + dx * step / steps)
                abs(ThreeFingerSwipe.ABS_MT_POSITION_Y, from + (to - from) * step / steps)
            }
            sync()
        }
        for (slot in 0 until fingers) {
            abs(ThreeFingerSwipe.ABS_MT_SLOT, slot)
            abs(ThreeFingerSwipe.ABS_MT_TRACKING_ID, -1)
            sync()
        }
        return out.array().copyOf(out.position())
    }

    private fun moves(bytes: ByteArray): List<ThreeFingerSwipe.Move> {
        val moves = mutableListOf<ThreeFingerSwipe.Move>()
        // The stream ends like a closed device: the watch stops there.
        runCatching { ThreeFingerSwipe(screen).watch(ByteArrayInputStream(bytes)) { moves += it } }
        return moves
    }

    @Test
    fun threeFingersUpMoveOnce() {
        val moves = moves(touch(3, from = 1500, to = 900))
        assertEquals(1, moves.size)
        assertEquals(-0.3f, moves.single().dy, 0.001f)
        assertEquals(0f, moves.single().dx, 0.001f)
        assertEquals(Swipes.Direction.Up, Swipes.direction(moves.single().dx, moves.single().dy, 0))
    }

    @Test
    fun threeFingersDown() {
        val move = moves(touch(3, from = 500, to = 1100)).single()
        assertEquals(Swipes.Direction.Down, Swipes.direction(move.dx, move.dy, 0))
    }

    @Test
    fun twoAndFourFingersAreNotSwipes() {
        assertEquals(emptyList<ThreeFingerSwipe.Move>(), moves(touch(2, from = 1500, to = 900)))
        assertEquals(emptyList<ThreeFingerSwipe.Move>(), moves(touch(4, from = 1500, to = 900)))
    }

    @Test
    fun slowDragIsNotASwipe() {
        assertEquals(emptyList<ThreeFingerSwipe.Move>(), moves(touch(3, from = 1500, to = 900, steps = 20, seconds = 0.1)))
    }

    @Test
    fun shortOrSidewaysMovesAreNotSwipes() {
        val short = moves(touch(3, from = 1500, to = 1400)).single()
        assertNull(Swipes.direction(short.dx, short.dy, 0))
        val diagonal = moves(touch(3, from = 1500, to = 900, dx = 500)).single()
        assertNull(Swipes.direction(diagonal.dx, diagonal.dy, 0))
    }

    @Test
    fun turnedScreenMapsTheMove() {
        // Landscape, top of the phone to the left (ROTATION_90): "up" on screen is toward the phone's right edge.
        assertEquals(Swipes.Direction.Up, Swipes.direction(0.4f, 0f, 1))
        assertEquals(Swipes.Direction.Down, Swipes.direction(-0.4f, 0f, 1))
        assertEquals(Swipes.Direction.Up, Swipes.direction(-0.4f, 0f, 3))
        assertEquals(Swipes.Direction.Up, Swipes.direction(0f, 0.4f, 2))
    }

    @Test
    fun findsTheTouchscreen() {
        val description = """
            add device 1: /dev/input/event5
              name:     "gpio_keys"
              events:
                KEY (0001): KEY_VOLUMEDOWN KEY_VOLUMEUP
              input props:
                <none>
            add device 2: /dev/input/event2
              name:     "sec_touchscreen"
              events:
                ABS (0003): ABS_MT_SLOT           : value 0, min 0, max 9, fuzz 0, flat 0, resolution 0
                            ABS_MT_POSITION_X     : value 0, min 0, max 4095, fuzz 0, flat 0, resolution 0
                            ABS_MT_POSITION_Y     : value 0, min 0, max 8191, fuzz 0, flat 0, resolution 0
                            ABS_MT_TRACKING_ID    : value 0, min 0, max 65535, fuzz 0, flat 0, resolution 0
              input props:
                INPUT_PROP_DIRECT
        """.trimIndent()
        assertEquals(Touchscreen("/dev/input/event2", 4095, 8191), Touchscreen.find(description))
        assertNotNull(Touchscreen.find(description))
        assertNull(Touchscreen.find("add device 1: /dev/input/event5\n  input props:\n    <none>"))
    }
}
