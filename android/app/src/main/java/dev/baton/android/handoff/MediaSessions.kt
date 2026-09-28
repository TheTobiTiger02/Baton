package dev.baton.android.handoff

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityContent
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.MediaActions
import dev.baton.android.protocol.Playback
import dev.baton.android.protocol.Wire
import kotlin.math.roundToInt
import java.io.ByteArrayOutputStream
import kotlin.math.abs
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Every app on the phone that publishes a media session: YouTube, Spotify, Netflix, Chrome playing
 * video. Reading them needs the notification-listener grant; that is also what lets Baton pause,
 * play and seek them.
 */
object MediaSessions {
    private const val TAG = "BatonMedia"
    private const val ARTWORK_PX = 192
    private const val MAX_SESSIONS = 6

    private val handler = Handler(Looper.getMainLooper())
    private var appContext: Context? = null
    private var manager: MediaSessionManager? = null
    private var listenerComponent: ComponentName? = null
    private val controllers = mutableListOf<Pair<MediaController, MediaController.Callback>>()
    private val artworkCache = mutableMapOf<String, Pair<Any?, String?>>()

    /** Called on the main thread whenever the set of sessions or their state changes. */
    var onChanged: (() -> Unit)? = null

    val isAvailable: Boolean get() = manager != null

    private val sessionsListener = MediaSessionManager.OnActiveSessionsChangedListener { bind(it.orEmpty()) }

    /** Starts watching; only works once the notification listener is connected. */
    fun start(context: Context, listener: ComponentName) = handler.post {
        if (manager != null) return@post
        val app = context.applicationContext
        val sessionManager = app.getSystemService(MediaSessionManager::class.java) ?: return@post
        try {
            sessionManager.addOnActiveSessionsChangedListener(sessionsListener, listener, handler)
            appContext = app
            manager = sessionManager
            listenerComponent = listener
            bind(sessionManager.getActiveSessions(listener))
        } catch (e: SecurityException) {
            Log.w(TAG, "Notification access is not granted; media sessions are unavailable.", e)
        }
    }

    fun stop() = handler.post {
        manager?.removeOnActiveSessionsChangedListener(sessionsListener)
        manager = null
        controllers.forEach { (controller, callback) -> controller.unregisterCallback(callback) }
        controllers.clear()
        onChanged?.invoke()
    }

    /** Activities for every session with something in it, playing first. Main thread. */
    fun activities(deviceId: String): List<Activity> {
        val context = appContext ?: return emptyList()
        return controllers.map { it.first }
            .filter { it.metadata?.title() != null && it.packageName != context.packageName }
            .sortedByDescending { it.playbackState?.isActive() == true }
            .mapNotNull { describe(context, deviceId, it) }
    }

    fun controllerFor(packageName: String): MediaController? =
        refreshControllers().firstOrNull { it.packageName == packageName }

    fun pause(packageName: String) {
        controllerFor(packageName)?.transportControls?.pause()
    }

    /** A remote command from another device for the app's session. Volume is the phone's media volume. */
    fun command(packageName: String, action: String, positionMs: Long?, volume: Double?): Boolean {
        val controller = controllerFor(packageName) ?: return false
        val controls = controller.transportControls
        when (action) {
            MediaActions.PLAY -> controls.play()
            MediaActions.PAUSE -> controls.pause()
            MediaActions.TOGGLE -> if (controller.playbackState?.isActive() == true) controls.pause() else controls.play()
            MediaActions.SEEK -> controls.seekTo(positionMs ?: return false)
            MediaActions.SKIP -> controls.seekTo(((controller.playbackState?.currentPosition() ?: 0L) + (positionMs ?: return false)).coerceAtLeast(0))
            MediaActions.NEXT -> controls.skipToNext()
            MediaActions.PREVIOUS -> controls.skipToPrevious()
            MediaActions.VOLUME -> {
                val audio = appContext?.getSystemService(android.media.AudioManager::class.java) ?: return false
                val max = audio.getStreamMaxVolume(android.media.AudioManager.STREAM_MUSIC)
                audio.setStreamVolume(android.media.AudioManager.STREAM_MUSIC, ((volume ?: return false) * max).roundToInt().coerceIn(0, max), 0)
            }
            else -> return false
        }
        return true
    }

    /**
     * Waits for a session matching [matches] to load (it has a duration), then seeks it to
     * [positionMs] when it is more than [toleranceMs] away and makes sure it plays. This is what
     * makes an app resume at the right second even when the link that opened it had no timestamp.
     */
    suspend fun reconcile(
        matches: (MediaController) -> Boolean,
        positionMs: Long,
        play: Boolean,
        timeoutMs: Long = 25_000,
        toleranceMs: Long = 3_000
    ): Boolean = withTimeoutOrNull(timeoutMs) {
        var controller = findLoaded(matches, positionMs)
        while (controller == null) {
            delay(500)
            controller = findLoaded(matches, positionMs)
        }

        val state = controller.playbackState
        if (positionMs > 0 && abs((state?.currentPosition() ?: 0L) - positionMs) > toleranceMs) {
            controller.transportControls.seekTo(positionMs)
        }
        if (play && state?.isActive() != true) controller.transportControls.play()
        Log.i(TAG, "Reconciled ${controller.packageName} to $positionMs ms")
        true
    } ?: false

    /** A matching session that has loaded its content (a duration), so a seek will stick. */
    private fun findLoaded(matches: (MediaController) -> Boolean, positionMs: Long): MediaController? =
        refreshControllers().firstOrNull { matches(it) && (it.duration() > 0 || positionMs <= 0) }

    private fun refreshControllers(): List<MediaController> {
        val sessionManager = manager ?: return emptyList()
        val component = listenerComponent ?: return emptyList()
        return runCatching { sessionManager.getActiveSessions(component) }.getOrDefault(emptyList())
    }

    private fun bind(active: List<MediaController>) {
        controllers.forEach { (controller, callback) -> controller.unregisterCallback(callback) }
        controllers.clear()
        active.take(MAX_SESSIONS).forEach { controller ->
            val callback = object : MediaController.Callback() {
                override fun onPlaybackStateChanged(state: PlaybackState?) {
                    onChanged?.invoke()
                }

                override fun onMetadataChanged(metadata: MediaMetadata?) {
                    onChanged?.invoke()
                }

                override fun onSessionDestroyed() {
                    onChanged?.invoke()
                }
            }
            controller.registerCallback(callback, handler)
            controllers += controller to callback
        }
        artworkCache.keys.retainAll(controllers.map { it.first.packageName }.toSet())
        onChanged?.invoke()
    }

    private fun describe(context: Context, deviceId: String, controller: MediaController): Activity? {
        val metadata = controller.metadata ?: return null
        val title = metadata.title() ?: return null
        val state = controller.playbackState
        val packageName = controller.packageName
        val known = KnownApps.fromPackage(packageName)
        if (known != null && !known.isBrowser && state?.isActive() == true) noteUsed(known.provider, packageName)
        val artist = metadata.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: metadata.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: metadata.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE)
        val mediaId = metadata.getString(MediaMetadata.METADATA_KEY_MEDIA_ID)
        val updatedAt = state?.lastPositionUpdateTime?.takeIf { it > 0 }
            ?.let { System.currentTimeMillis() - (SystemClock.elapsedRealtime() - it) }
            ?: System.currentTimeMillis()
        val playing = state?.isActive() == true
        val query = listOfNotNull(title, artist).joinToString(" ")

        return Activity(
            id = "media:$packageName",
            deviceId = deviceId,
            kind = if (known?.isBrowser == true) ActivityKind.WebMedia else ActivityKind.AppMedia,
            title = title,
            app = ActivityApp(appName(context, packageName), packageName),
            updatedAt = Wire.time(if (playing) System.currentTimeMillis() else updatedAt),
            subtitle = artist,
            artworkJpegBase64 = artwork(packageName, metadata),
            content = ActivityContent(
                provider = when {
                    known == null -> "unknown"
                    known.isBrowser -> "web"
                    else -> known.provider
                },
                id = mediaId?.takeIf { it.startsWith("spotify:") },
                query = query
            ),
            playback = Playback(
                positionMs = (state?.position ?: 0L).coerceAtLeast(0L),
                durationMs = Playback.plausibleDuration(metadata.getLong(MediaMetadata.METADATA_KEY_DURATION)),
                playing = playing,
                rate = (state?.playbackSpeed?.takeIf { it > 0 } ?: 1f).toDouble(),
                capturedAt = Wire.time(updatedAt)
            )
        )
    }

    /** Small JPEG of the cover, re-encoded only when the bitmap changes. */
    private fun artwork(packageName: String, metadata: MediaMetadata): String? {
        val bitmap = metadata.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: metadata.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: metadata.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
        artworkCache[packageName]?.let { (source, encoded) -> if (source === bitmap) return encoded }
        val encoded = bitmap?.let {
            runCatching {
                val scale = ARTWORK_PX.toFloat() / maxOf(it.width, it.height)
                val scaled = if (scale < 1f) Bitmap.createScaledBitmap(it, (it.width * scale).toInt(), (it.height * scale).toInt(), true) else it
                val bytes = ByteArrayOutputStream().use { out ->
                    scaled.compress(Bitmap.CompressFormat.JPEG, 82, out)
                    out.toByteArray()
                }
                Base64.encodeToString(bytes, Base64.NO_WRAP)
            }.getOrNull()
        }
        artworkCache[packageName] = bitmap to encoded
        return encoded
    }

    /** The build of each known app (YouTube, ReVanced...) that last had a session: the one in use. */
    fun lastUsed(provider: String): String? =
        appContext?.getSharedPreferences("baton_apps", Context.MODE_PRIVATE)?.getString("last_used_$provider", null)

    /** A known app came to the front: that build is the one in use. */
    fun noteForeground(packageName: String) {
        val known = KnownApps.fromPackage(packageName) ?: return
        if (!known.isBrowser) noteUsed(known.provider, packageName)
    }

    /** The session of whichever build of [app] has one, e.g. ReVanced rather than stock YouTube. */
    fun controllerForApp(app: KnownApp): MediaController? =
        refreshControllers().firstOrNull { it.packageName in app.packages || KnownApps.fromPackage(it.packageName) == app }

    private fun noteUsed(provider: String, packageName: String) {
        val prefs = appContext?.getSharedPreferences("baton_apps", Context.MODE_PRIVATE) ?: return
        if (prefs.getString("last_used_$provider", null) != packageName) prefs.edit().putString("last_used_$provider", packageName).apply()
    }

    fun appName(context: Context, packageName: String): String = try {
        val info = context.packageManager.getApplicationInfo(packageName, 0)
        context.packageManager.getApplicationLabel(info).toString()
    } catch (_: PackageManager.NameNotFoundException) {
        packageName
    }

    private fun MediaMetadata.title(): String? =
        (getString(MediaMetadata.METADATA_KEY_TITLE) ?: getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE))?.takeIf { it.isNotBlank() }

    private fun MediaController.duration(): Long = Playback.plausibleDuration(metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L)

    private fun PlaybackState.isActive(): Boolean =
        state == PlaybackState.STATE_PLAYING || state == PlaybackState.STATE_BUFFERING

    /** The position now, extrapolated from the state's last update while playing. */
    fun PlaybackState.currentPosition(): Long {
        if (state != PlaybackState.STATE_PLAYING || lastPositionUpdateTime <= 0) return position
        return position + ((SystemClock.elapsedRealtime() - lastPositionUpdateTime) * playbackSpeed).toLong()
    }
}
