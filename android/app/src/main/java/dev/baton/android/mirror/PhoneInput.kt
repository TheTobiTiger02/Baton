package dev.baton.android.mirror

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Context
import android.graphics.Path
import android.os.SystemClock
import android.util.Log
import android.view.KeyEvent
import android.view.WindowManager
import dev.baton.android.handoff.BatonAccessibilityService
import dev.baton.android.stream.ControlMessage
import dev.baton.android.stream.ControlMessageCodec

/**
 * Plays the PC's mouse and keyboard into this phone while it is mirrored: clicks and drags as
 * accessibility gestures, text and editing keys into the focused field, Back/Home/Recents as
 * global actions. Main thread only; gesture callbacks arrive there too.
 *
 * Drags are continued strokes. A continuation may only follow a completed segment, so moves that
 * arrive while one is in flight are merged into the next segment instead of queued one by one,
 * which would add lag that grows with the speed of the drag.
 */
class PhoneInput(private val context: Context) {
    private class Pointer(var lastX: Float, var lastY: Float, var stroke: GestureDescription.StrokeDescription?) {
        var inFlight = false
        var ending = false
        var lastEventAt = SystemClock.uptimeMillis()
        val pending = ArrayList<Pair<Float, Float>>()
    }

    private var pointer: Pointer? = null
    private val service: AccessibilityService? get() = BatonAccessibilityService.instance
    private val touchscreen = ShizukuTouches()

    fun apply(message: ControlMessage) {
        val shell = ShizukuAccess.shell
        when (message) {
            is ControlMessage.Touch -> if (shell != null) shizukuTouch(shell, message) else touch(message)
            is ControlMessage.Scroll -> if (shell != null) shizukuScroll(shell, message) else scroll(message)
            is ControlMessage.Key -> {
                val accessibility = service
                if (accessibility != null) {
                    if (message.action == ControlMessageCodec.KEY_DOWN) FocusedFieldEditor(accessibility).key(message.keyCode, message.metaState)
                } else if (shell != null) {
                    val action = if (message.action == ControlMessageCodec.KEY_DOWN) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP
                    runCatching { shell.injectKey(action, message.keyCode, message.metaState) }
                }
            }
            is ControlMessage.Text -> service?.let { FocusedFieldEditor(it).insert(message.value) }
            is ControlMessage.Navigate -> navigate(message.target, shell)
            else -> Unit
        }
    }

    /** Real touchscreen events through Shizuku: several fingers, and apps see an ordinary touch. */
    private fun shizukuTouch(shell: IShizukuShell, message: ControlMessage.Touch) {
        val point = mapToDisplay(message.x, message.y, message.frameWidth, message.frameHeight) ?: return
        val event = when (message.action) {
            ControlMessageCodec.TOUCH_DOWN -> touchscreen.down(message.pointerId, point)
            ControlMessageCodec.TOUCH_MOVE -> touchscreen.move(message.pointerId, point)
            ControlMessageCodec.TOUCH_UP -> touchscreen.up(message.pointerId, point, cancel = false)
            ControlMessageCodec.TOUCH_CANCEL -> touchscreen.up(message.pointerId, point, cancel = true)
            else -> null
        } ?: return
        val injected = runCatching { shell.injectTouch(event.action, event.downTime, event.ids, event.xs, event.ys) }
            .onFailure { Log.w(TAG, "Shizuku helper unreachable", it) }
            .getOrDefault(false)
        if (!injected) Log.w(TAG, "Touch not injected (action ${event.action})")
    }

    private fun shizukuScroll(shell: IShizukuShell, message: ControlMessage.Scroll) {
        val point = mapToDisplay(message.x, message.y, message.frameWidth, message.frameHeight) ?: return
        val injected = runCatching { shell.injectScroll(point.first, point.second, message.horizontal.toFloat(), message.vertical.toFloat()) }
            .onFailure { Log.w(TAG, "Shizuku helper unreachable", it) }
            .getOrDefault(false)
        if (!injected) Log.w(TAG, "Scroll not injected")
    }

    private fun touch(message: ControlMessage.Touch) {
        val point = mapToDisplay(message.x, message.y, message.frameWidth, message.frameHeight) ?: return
        val accessibility = service ?: return unavailable()
        when (message.action) {
            ControlMessageCodec.TOUCH_DOWN -> {
                val start = Path().apply {
                    moveTo(point.first, point.second)
                    lineTo(point.first, point.second)
                }
                val stroke = GestureDescription.StrokeDescription(start, 0, SEGMENT_MIN_MS, true)
                val state = Pointer(point.first, point.second, stroke)
                pointer = state
                dispatch(accessibility, state, stroke)
            }
            ControlMessageCodec.TOUCH_MOVE -> {
                val state = pointer ?: return
                state.pending += point
                if (!state.inFlight) flush(accessibility, state)
            }
            ControlMessageCodec.TOUCH_UP, ControlMessageCodec.TOUCH_CANCEL -> {
                val state = pointer ?: return
                pointer = null
                state.pending += point
                state.ending = true
                if (!state.inFlight) flush(accessibility, state)
            }
        }
    }

    // A held button needs nothing extra: a stroke that will continue keeps the finger down until
    // the next segment, so holding the mouse still is an Android long press. (Padding the wait with
    // still segments gets them cancelled.)
    private fun flush(accessibility: AccessibilityService, state: Pointer) {
        val previous = state.stroke ?: return
        if (state.pending.isEmpty() && !state.ending) return
        val path = Path().apply {
            moveTo(state.lastX, state.lastY)
            if (state.pending.isEmpty()) lineTo(state.lastX, state.lastY)
            for ((x, y) in state.pending) lineTo(x, y)
        }
        state.pending.lastOrNull()?.let { (x, y) ->
            state.lastX = x
            state.lastY = y
        }
        state.pending.clear()

        // Replay each segment at about the speed it happened, so flings still fling.
        val now = SystemClock.uptimeMillis()
        val duration = (now - state.lastEventAt).coerceIn(SEGMENT_MIN_MS, SEGMENT_MAX_MS)
        state.lastEventAt = now
        val next = previous.continueStroke(path, 0, duration, !state.ending)
        state.stroke = next
        dispatch(accessibility, state, next)
    }

    private fun dispatch(accessibility: AccessibilityService, state: Pointer, stroke: GestureDescription.StrokeDescription) {
        state.inFlight = true
        val dispatched = accessibility.dispatchGesture(
            GestureDescription.Builder().addStroke(stroke).build(),
            object : AccessibilityService.GestureResultCallback() {
                override fun onCompleted(gestureDescription: GestureDescription?) {
                    state.inFlight = false
                    if (!stroke.willContinue()) return
                    if (state.pending.isNotEmpty() || state.ending) flush(accessibility, state)
                }

                override fun onCancelled(gestureDescription: GestureDescription?) {
                    // A real touch on the phone took over; drop this drag.
                    state.inFlight = false
                    state.pending.clear()
                    if (pointer === state) pointer = null
                }
            },
            null
        )
        if (!dispatched) {
            state.inFlight = false
            Log.w(TAG, "The system refused a gesture.")
        }
    }

    /** One wheel notch moves the content a comfortable fixed distance; +1 vertical is wheel up. */
    private fun scroll(message: ControlMessage.Scroll) {
        val point = mapToDisplay(message.x, message.y, message.frameWidth, message.frameHeight) ?: return
        val accessibility = service ?: return unavailable()
        val step = context.resources.displayMetrics.density * SCROLL_DP_PER_NOTCH
        val path = Path().apply {
            moveTo(point.first, point.second)
            lineTo(point.first - message.horizontal * step, point.second + message.vertical * step)
        }
        accessibility.dispatchGesture(
            GestureDescription.Builder().addStroke(GestureDescription.StrokeDescription(path, 0, SCROLL_DURATION_MS)).build(),
            null, null
        )
    }

    private fun navigate(target: Int, shell: IShizukuShell?) {
        val accessibility = service
        if (accessibility == null && shell != null) {
            val key = when (target) {
                ControlMessageCodec.NAV_BACK -> KeyEvent.KEYCODE_BACK
                ControlMessageCodec.NAV_HOME -> KeyEvent.KEYCODE_HOME
                ControlMessageCodec.NAV_RECENTS -> KeyEvent.KEYCODE_APP_SWITCH
                ControlMessageCodec.NAV_NOTIFICATIONS -> KeyEvent.KEYCODE_NOTIFICATION
                ControlMessageCodec.NAV_POWER -> KeyEvent.KEYCODE_SLEEP
                else -> return
            }
            runCatching {
                shell.injectKey(KeyEvent.ACTION_DOWN, key, 0)
                shell.injectKey(KeyEvent.ACTION_UP, key, 0)
            }
            return
        }
        accessibility ?: return unavailable()
        accessibility.performGlobalAction(when (target) {
            ControlMessageCodec.NAV_BACK -> AccessibilityService.GLOBAL_ACTION_BACK
            ControlMessageCodec.NAV_HOME -> AccessibilityService.GLOBAL_ACTION_HOME
            ControlMessageCodec.NAV_RECENTS -> AccessibilityService.GLOBAL_ACTION_RECENTS
            ControlMessageCodec.NAV_NOTIFICATIONS -> AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS
            ControlMessageCodec.NAV_POWER -> AccessibilityService.GLOBAL_ACTION_LOCK_SCREEN
            else -> return
        })
    }

    /**
     * Frame pixels to display pixels. Null when the frame's orientation no longer matches the
     * display: a position computed before a rotation must be dropped, not applied elsewhere.
     */
    private fun mapToDisplay(x: Int, y: Int, frameWidth: Int, frameHeight: Int): Pair<Float, Float>? {
        if (frameWidth <= 0 || frameHeight <= 0) return null
        val bounds = context.getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds
        val width = bounds.width()
        val height = bounds.height()
        if ((frameWidth > frameHeight) != (width > height)) return null
        return (x.toFloat() / frameWidth * width).coerceIn(0f, width - 1f) to
            (y.toFloat() / frameHeight * height).coerceIn(0f, height - 1f)
    }

    private var lastWarning = 0L

    private fun unavailable() {
        val now = SystemClock.uptimeMillis()
        if (now - lastWarning > 5_000) {
            lastWarning = now
            Log.w(TAG, "PC input dropped: Baton's accessibility service is off.")
        }
    }

    private companion object {
        const val TAG = "BatonPhoneInput"
        const val SEGMENT_MIN_MS = 8L
        const val SEGMENT_MAX_MS = 100L
        const val SCROLL_DP_PER_NOTCH = 90f
        const val SCROLL_DURATION_MS = 120L
    }
}
