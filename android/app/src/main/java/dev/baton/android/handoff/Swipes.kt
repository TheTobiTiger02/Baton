package dev.baton.android.handoff

import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.os.VibrationEffect
import android.util.Log
import android.os.Vibrator
import android.view.Display
import android.view.Surface
import dev.baton.android.BatonSettings
import dev.baton.android.link.Link

/**
 * Three-finger swipes (seen by the Shizuku helper, see [dev.baton.android.mirror.ThreeFingerSwipe]):
 * up sends what's on this phone to the PC, down continues the PC's activity here. Off by default
 * and per app.
 */
object Swipes {
    enum class Direction { Up, Down }

    /** How far, as a fraction of the screen, three fingers must travel to count as a swipe. */
    private const val MIN_TRAVEL = 0.15f
    private const val REPEAT_AFTER_MS = 1500L

    private val main by lazy { Handler(Looper.getMainLooper()) }
    private var lastAt = 0L

    /** Called from the helper's binder thread with the move in the screen's natural orientation. */
    fun onSwipe(context: Context, dx: Float, dy: Float) {
        val rotation = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)?.rotation ?: Surface.ROTATION_0
        val direction = direction(dx, dy, rotation) ?: return
        Log.i("BatonSwipes", "Three-finger swipe ${direction.name.lowercase()}")
        main.post { act(context, direction) }
    }

    private fun act(context: Context, direction: Direction) {
        if (!BatonSettings.swipes(context) || !Link.isReady) return
        val app = BrowserPages.foregroundPackage
        if (app != null && app in BatonSettings.swipesOffApps(context)) return
        val now = SystemClock.elapsedRealtime()
        if (now - lastAt < REPEAT_AFTER_MS) return
        lastAt = now
        // The tick only confirms the swipe: never let it stop the handoff.
        runCatching { context.getSystemService(Vibrator::class.java)?.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK)) }
        when (direction) {
            Direction.Up -> HandoffEngine.sendTo()
            Direction.Down -> HandoffEngine.pullFromPc()
        }
    }

    /**
     * The swipe as the user sees it on a screen turned by [rotation] (a [Surface] rotation), or
     * null when the move is too short or too sideways to be one.
     */
    fun direction(dx: Float, dy: Float, rotation: Int): Direction? {
        val (sideways, vertical) = when (rotation) {
            Surface.ROTATION_90 -> dy to -dx
            Surface.ROTATION_180 -> -dx to -dy
            Surface.ROTATION_270 -> -dy to dx
            else -> dx to dy
        }
        if (kotlin.math.abs(vertical) < MIN_TRAVEL || kotlin.math.abs(sideways) > kotlin.math.abs(vertical) / 2) return null
        return if (vertical < 0) Direction.Up else Direction.Down
    }
}
