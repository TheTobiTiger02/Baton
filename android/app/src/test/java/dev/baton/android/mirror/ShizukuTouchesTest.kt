package dev.baton.android.mirror

import android.view.MotionEvent
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ShizukuTouchesTest {
    @Test
    fun twoFingersBecomeDownPointerDownMovePointerUpUp() {
        val touches = ShizukuTouches()
        val first = touches.down(101, 10f to 20f, now = 5)!!
        assertEquals(MotionEvent.ACTION_DOWN, first.action)
        assertEquals(5L, first.downTime)
        assertArrayEquals(intArrayOf(0), first.ids)

        val second = touches.down(202, 30f to 40f, now = 9)!!
        assertEquals(MotionEvent.ACTION_POINTER_DOWN or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT), second.action)
        assertEquals(5L, second.downTime)
        assertArrayEquals(intArrayOf(0, 1), second.ids)

        val move = touches.move(202, 35f to 45f)!!
        assertEquals(MotionEvent.ACTION_MOVE, move.action)
        assertEquals(35f, move.xs[1])

        val firstUp = touches.up(101, 10f to 20f, cancel = false)!!
        assertEquals(MotionEvent.ACTION_POINTER_UP or (0 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT), firstUp.action)
        assertEquals(2, firstUp.ids.size)

        val last = touches.up(202, 35f to 45f, cancel = false)!!
        assertEquals(MotionEvent.ACTION_UP, last.action)
        assertArrayEquals(intArrayOf(1), last.ids)

        assertNull(touches.move(202, 0f to 0f))
    }

    @Test
    fun freedSlotsAreReused() {
        val touches = ShizukuTouches()
        touches.down(1, 0f to 0f, now = 0)
        touches.down(2, 0f to 0f, now = 0)
        touches.up(1, 0f to 0f, cancel = false)
        assertArrayEquals(intArrayOf(1, 0), touches.down(3, 0f to 0f, now = 0)!!.ids)
    }
}
