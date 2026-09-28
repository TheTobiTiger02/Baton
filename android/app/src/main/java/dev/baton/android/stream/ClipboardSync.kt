package dev.baton.android.stream

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import dev.baton.android.mirror.ShizukuAccess

/**
 * Keeps this phone's and the PC's clipboards the same while one shows the other: text copied on
 * either side can be pasted on the other. Only during those sessions, and never a clip Android
 * marks as sensitive (passwords).
 *
 * Android lets an app read the clipboard only while it is in front. Showing a PC window, Baton is;
 * mirrored to the PC, it is not, so it reads through the Shizuku helper when there is one, and
 * otherwise gets the selection from the mirror's Ctrl+C ([copied]).
 */
object ClipboardSync {
    private const val POLL_MS = 1_000L

    private val main = Handler(Looper.getMainLooper())
    private var clipboard: ClipboardManager? = null
    private var sessions = 0
    private var last: String? = null
    private var baseline = true

    private val listener = ClipboardManager.OnPrimaryClipChangedListener { readLocal()?.let(::offer) }

    private val poll = object : Runnable {
        override fun run() {
            if (sessions == 0) return
            val shell = ShizukuAccess.shell
            val text = shell?.let { runCatching { it.clipboardText() }.getOrNull() }
            // The first read is what was there before the session: not something just copied.
            if (baseline) {
                if (text != null && last == null) last = text
                baseline = false
            } else if (text != null) {
                offer(text)
            }
            main.postDelayed(this, POLL_MS)
        }
    }

    /** A mirror or stream session began; [stop] ends it. Sessions may overlap. */
    fun start(context: Context) {
        main.post {
            val manager = context.applicationContext.getSystemService(ClipboardManager::class.java)
            clipboard = manager
            if (sessions++ == 0) {
                baseline = true
                manager.addPrimaryClipChangedListener(listener)
                main.post(poll)
            }
        }
    }

    fun stop() {
        main.post {
            if (sessions == 0 || --sessions > 0) return@post
            clipboard?.removePrimaryClipChangedListener(listener)
            main.removeCallbacks(poll)
            last = null
        }
    }

    /** The PC's copy, from the media channel. */
    fun onRemote(text: String) {
        main.post {
            val manager = clipboard ?: return@post
            if (sessions == 0) return@post
            // Remembered first: writing it raises a clipboard change that must not echo back.
            last = text
            runCatching { manager.setPrimaryClip(ClipData.newPlainText("Baton", text)) }
        }
    }

    /** Text the mirror's Ctrl+C selected, read through accessibility where the clipboard can't be. */
    fun copied(text: String) {
        main.post { if (sessions > 0) offer(text) }
    }

    private fun offer(text: String) {
        if (text.isEmpty() || text == last || !ControlMessageCodec.fitsText(text)) return
        last = text
        MediaChannel.send(StreamProtocol.CHANNEL_CONTROL, ControlMessageCodec.clipboard(text))
    }

    private fun readLocal(): String? {
        val clip = runCatching { clipboard?.primaryClip }.getOrNull() ?: return null
        if (isSensitive(clip.description)) return null
        return clip.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
    }

    fun isSensitive(description: ClipDescription?): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            description?.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE) == true
}
