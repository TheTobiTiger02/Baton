package dev.baton.android.mirror

import android.os.SystemClock
import android.view.MotionEvent

/**
 * The fingers the PC has down, turned into touchscreen events: each change becomes one
 * MotionEvent action plus every pointer's position, as Android expects. The PC's pointer ids
 * become small slots, since MotionEvent ids must stay below 32.
 */
class ShizukuTouches {
    class Event(val action: Int, val downTime: Long, val ids: IntArray, val xs: FloatArray, val ys: FloatArray)

    private class Finger(val slot: Int, var x: Float, var y: Float)

    private val fingers = LinkedHashMap<Long, Finger>()
    private var downTime = 0L

    fun down(pointerId: Long, point: Pair<Float, Float>, now: Long = SystemClock.uptimeMillis()): Event? {
        if (pointerId in fingers) return move(pointerId, point)
        val slot = (0 until MAX_FINGERS).firstOrNull { slot -> fingers.values.none { it.slot == slot } } ?: return null
        if (fingers.isEmpty()) downTime = now
        fingers[pointerId] = Finger(slot, point.first, point.second)
        val action = if (fingers.size == 1) MotionEvent.ACTION_DOWN else pointerAction(MotionEvent.ACTION_POINTER_DOWN, pointerId)
        return snapshot(action)
    }

    fun move(pointerId: Long, point: Pair<Float, Float>): Event? {
        val finger = fingers[pointerId] ?: return null
        finger.x = point.first
        finger.y = point.second
        return snapshot(MotionEvent.ACTION_MOVE)
    }

    fun up(pointerId: Long, point: Pair<Float, Float>, cancel: Boolean): Event? {
        val finger = fingers[pointerId] ?: return null
        finger.x = point.first
        finger.y = point.second
        val action = when {
            fingers.size > 1 -> pointerAction(MotionEvent.ACTION_POINTER_UP, pointerId)
            cancel -> MotionEvent.ACTION_CANCEL
            else -> MotionEvent.ACTION_UP
        }
        return snapshot(action).also { fingers.remove(pointerId) }
    }

    private fun pointerAction(action: Int, pointerId: Long): Int =
        action or (fingers.keys.indexOf(pointerId) shl MotionEvent.ACTION_POINTER_INDEX_SHIFT)

    private fun snapshot(action: Int): Event {
        val list = fingers.values.toList()
        return Event(action, downTime, IntArray(list.size) { list[it].slot }, FloatArray(list.size) { list[it].x }, FloatArray(list.size) { list[it].y })
    }

    private companion object {
        const val MAX_FINGERS = 10
    }
}
