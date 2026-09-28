package dev.baton.android.mirror

import android.content.ClipData
import android.content.ClipDescription
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import android.util.Log
import android.view.InputDevice
import android.view.InputEvent
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.MotionEvent
import kotlin.system.exitProcess

/**
 * Baton's helper process, started by Shizuku as the shell user (like adb shell): it may run
 * appops and settings, and inject input as a real touchscreen. Nothing here runs in the app.
 */
class ShizukuShell : IShizukuShell.Stub() {
    /** InputManagerGlobal on Android 14+, InputManager before; both have injectInputEvent. */
    private val injector: Pair<Any, java.lang.reflect.Method>? by lazy {
        listOf("android.hardware.input.InputManagerGlobal", "android.hardware.input.InputManager").firstNotNullOfOrNull { name ->
            runCatching {
                val type = Class.forName(name)
                val instance = type.getMethod("getInstance").invoke(null) ?: return@runCatching null
                instance to type.getMethod("injectInputEvent", InputEvent::class.java, Int::class.javaPrimitiveType)
            }.getOrNull()
        }
    }

    override fun destroy() {
        exitProcess(0)
    }

    override fun exec(command: Array<String>): String {
        val process = ProcessBuilder(*command).redirectErrorStream(true).start()
        val output = process.inputStream.bufferedReader().use { it.readText() }
        process.waitFor()
        return output
    }

    override fun injectTouch(action: Int, downTime: Long, ids: IntArray, xs: FloatArray, ys: FloatArray): Boolean {
        val count = ids.size
        if (count == 0 || xs.size != count || ys.size != count) return false
        val properties = Array(count) { index ->
            MotionEvent.PointerProperties().apply {
                id = ids[index]
                toolType = MotionEvent.TOOL_TYPE_FINGER
            }
        }
        val coords = Array(count) { index ->
            MotionEvent.PointerCoords().apply {
                x = xs[index]
                y = ys[index]
                pressure = 1f
                size = 1f
            }
        }
        val event = MotionEvent.obtain(downTime, SystemClock.uptimeMillis(), action, count, properties, coords,
            0, 0, 1f, 1f, VIRTUAL_DEVICE, 0, InputDevice.SOURCE_TOUCHSCREEN, 0)
        return inject(event).also { event.recycle() }
    }

    override fun injectScroll(x: Float, y: Float, horizontal: Float, vertical: Float): Boolean {
        val properties = arrayOf(MotionEvent.PointerProperties().apply {
            id = 0
            toolType = MotionEvent.TOOL_TYPE_MOUSE
        })
        val coords = arrayOf(MotionEvent.PointerCoords().apply {
            this.x = x
            this.y = y
            setAxisValue(MotionEvent.AXIS_VSCROLL, vertical)
            setAxisValue(MotionEvent.AXIS_HSCROLL, horizontal)
        })
        val now = SystemClock.uptimeMillis()
        val event = MotionEvent.obtain(now, now, MotionEvent.ACTION_SCROLL, 1, properties, coords,
            0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_MOUSE, 0)
        onDefaultDisplay(event)
        return inject(event).also { event.recycle() }
    }

    override fun injectKey(action: Int, keyCode: Int, metaState: Int): Boolean {
        val now = SystemClock.uptimeMillis()
        return inject(KeyEvent(now, now, action, keyCode, 0, metaState, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD))
    }

    private val clipboard: Any? by lazy {
        runCatching {
            val binder = Class.forName("android.os.ServiceManager").getMethod("getService", String::class.java)
                .invoke(null, "clipboard") as IBinder
            Class.forName("android.content.IClipboard\$Stub").getMethod("asInterface", IBinder::class.java).invoke(null, binder)
        }.onFailure { Log.w(TAG, "No clipboard service", it) }.getOrNull()
    }
    private var clipTimestamp = Long.MIN_VALUE

    override fun clipboardText(): String? {
        // The description is free to read; the clip itself is fetched once per copy, since reading
        // it may show Android's "pasted from your clipboard" notice.
        val description = clipboardCall("getPrimaryClipDescription") as? ClipDescription ?: return null
        if (description.timestamp == clipTimestamp) return null
        clipTimestamp = description.timestamp
        val sensitive = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            description.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE) == true
        if (sensitive) return null
        val clip = clipboardCall("getPrimaryClip") as? ClipData ?: return null
        return clip.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
    }

    /**
     * Calls an IClipboard getter as the shell package. Its parameters grew over Android versions
     * (package; + user; + attribution tag; + device), so they are filled by type.
     */
    private fun clipboardCall(name: String): Any? {
        val service = clipboard ?: return null
        val method = service.javaClass.methods.firstOrNull { it.name == name } ?: return null
        val args = method.parameterTypes.mapIndexed { index, type ->
            when (type) {
                String::class.java -> if (index == 0) SHELL_PACKAGE else null
                Int::class.javaPrimitiveType -> 0
                else -> null
            }
        }
        return runCatching { method.invoke(service, *args.toTypedArray()) }
            .onFailure { Log.w(TAG, "$name failed", it) }
            .getOrNull()
    }

    /** Hover-type events (the wheel) are dropped without a display; touches get one assigned. */
    private fun onDefaultDisplay(event: InputEvent) {
        runCatching { InputEvent::class.java.getMethod("setDisplayId", Int::class.javaPrimitiveType).invoke(event, 0) }
    }

    private fun inject(event: InputEvent): Boolean {
        val (instance, method) = injector ?: return false.also { Log.w(TAG, "No input manager to inject with") }
        return runCatching { method.invoke(instance, event, INJECT_ASYNC) as Boolean }
            .onFailure { Log.w(TAG, "Injecting input failed", it) }
            .getOrDefault(false)
    }

    private companion object {
        const val TAG = "BatonShizukuShell"
        const val VIRTUAL_DEVICE = -1
        const val INJECT_ASYNC = 0
        const val SHELL_PACKAGE = "com.android.shell"
    }
}
