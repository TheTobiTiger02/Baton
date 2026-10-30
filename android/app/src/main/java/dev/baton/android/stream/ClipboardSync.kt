package dev.baton.android.stream

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import dev.baton.android.BatonSettings
import dev.baton.android.mirror.ShizukuAccess

/**
 * Keeps this phone's and the PC's clipboards the same: text copied on either side can be pasted
 * on the other. While connected when "Sync clipboard" is on (the default), and always during a
 * mirror or stream; never a clip Android marks as sensitive (passwords).
 *
 * Android lets an app read the clipboard only while it is in front. Showing a PC window, Baton is.
 * Otherwise it reads through the Shizuku helper once a second when there is one; without it, a
 * copy reaches the PC when Baton next comes to the front ([onAppForeground]), and during a mirror
 * the selection comes from the mirror's Ctrl+C ([copied]).
 */
object ClipboardSync {
    private const val POLL_MS = 1_000L
    private const val PREFS = "baton_clipboard"
    private const val KEY_SYNCED_AT = "synced_at"

    private val main = Handler(Looper.getMainLooper())
    private var context: Context? = null
    private var clipboard: ClipboardManager? = null
    private var sessions = 0
    private var linked = false
    private var last: String? = null
    private var baseline = true

    private val active: Boolean get() = sessions > 0 || linked

    private val listener = ClipboardManager.OnPrimaryClipChangedListener { readLocal()?.let(::offer) }

    private val poll = object : Runnable {
        override fun run() {
            if (!active) return
            val shell = ShizukuAccess.shell
            val text = shell?.let { runCatching { it.clipboardText() }.getOrNull() }
            // The first read is what was there already: not something just copied. It only counts
            // once it succeeds: right after Baton starts the helper may not answer yet, and the
            // clip read later would otherwise go to the PC as if it were new.
            if (baseline) {
                if (text != null) {
                    if (last == null) last = text
                    baseline = false
                }
            } else if (text != null) {
                offer(text)
            }
            main.postDelayed(this, POLL_MS)
        }
    }

    /** A mirror or stream session began; [stop] ends it. Sessions may overlap. */
    fun start(context: Context) {
        main.post {
            val wasActive = active
            sessions++
            if (!wasActive) begin(context)
        }
    }

    fun stop() {
        main.post {
            if (sessions == 0) return@post
            sessions--
            if (!active) end()
        }
    }

    /** The link to the PC came up or went down; syncs all along when the setting allows. */
    fun linked(context: Context, up: Boolean) {
        main.post {
            val wanted = up && BatonSettings.syncClipboard(context)
            if (wanted == linked) return@post
            val wasActive = active
            linked = wanted
            when {
                !wasActive && active -> begin(context)
                wasActive && !active -> end()
            }
        }
    }

    /**
     * Baton came to the front, where Android lets it read the clipboard: a copy made since the
     * last one synced goes to the PC. The copy's own time decides, so an old clip is never resent.
     */
    fun onAppForeground(context: Context) {
        main.post {
            if (!active) return@post
            val manager = clipboard ?: return@post
            val clip = runCatching { manager.primaryClip }.getOrNull() ?: return@post
            val copiedAt = clip.description?.timestamp ?: return@post
            if (copiedAt <= syncedAt(context)) return@post
            readLocal()?.let(::offer)
        }
    }

    /** The PC's copy, from the media channel. */
    fun onRemote(text: String) {
        main.post {
            val manager = clipboard ?: return@post
            if (!active) return@post
            // Remembered first: writing it raises a clipboard change that must not echo back.
            last = text
            runCatching { manager.setPrimaryClip(ClipData.newPlainText("Baton", text)) }
            context?.let(::markSynced)
            // How much, never what: the log is shared in reports.
            dev.baton.android.link.LinkDiagnostics.record("clipboard.from-pc", "${text.length} characters")
        }
    }

    /** Text the mirror's Ctrl+C selected, read through accessibility where the clipboard can't be. */
    fun copied(text: String) {
        main.post { if (active) offer(text) }
    }

    private fun begin(context: Context) {
        val app = context.applicationContext
        this.context = app
        val manager = app.getSystemService(ClipboardManager::class.java)
        clipboard = manager
        baseline = true
        manager.addPrimaryClipChangedListener(listener)
        main.post(poll)
    }

    private fun end() {
        clipboard?.removePrimaryClipChangedListener(listener)
        main.removeCallbacks(poll)
        last = null
    }

    private fun offer(text: String) {
        if (text.isEmpty() || text == last || !ControlMessageCodec.fitsText(text)) return
        if (!MediaChannel.send(StreamProtocol.CHANNEL_CONTROL, ControlMessageCodec.clipboard(text))) return
        last = text
        context?.let(::markSynced)
        dev.baton.android.link.LinkDiagnostics.record("clipboard.to-pc", "${text.length} characters")
    }

    private fun readLocal(): String? {
        val clip = runCatching { clipboard?.primaryClip }.getOrNull() ?: return null
        if (isSensitive(clip.description)) return null
        return clip.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
    }

    private fun syncedAt(context: Context): Long =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getLong(KEY_SYNCED_AT, 0L)

    private fun markSynced(context: Context) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putLong(KEY_SYNCED_AT, System.currentTimeMillis()).apply()
    }

    fun isSensitive(description: ClipDescription?): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            description?.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE) == true
}
