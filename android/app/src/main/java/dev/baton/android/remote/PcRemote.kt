package dev.baton.android.remote

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.drawable.Icon
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.util.Base64
import dev.baton.android.R
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.link.Link
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.MediaActions
import dev.baton.android.protocol.PeerInfo
import dev.baton.android.protocol.Wire
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * The PC's playback as a media session on the phone: it shows in Android's media controls, on
 * the lock screen and in the notification shade, and headset or watch buttons control the PC.
 * Nothing plays on the phone; every button becomes a `media.command` to the PC.
 */
object PcRemote {
    private const val CHANNEL = "pc-remote"
    private const val NOTIFICATION_ID = 7
    private const val RECENT_MS = 10 * 60_000L
    private const val SKIP_MS = 10_000L

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var job: Job? = null
    private var session: MediaSession? = null
    private var appContext: Context? = null
    private var current: Pair<String, Activity>? = null
    private var shownKey: String? = null
    private var artworkKey: String? = null
    private var artwork: Bitmap? = null

    fun start(context: Context) {
        if (job != null) return
        appContext = context.applicationContext
        job = scope.launch { Link.peers.collect { update(it) } }
    }

    fun stop() {
        job?.cancel()
        job = null
        hide()
    }

    /** Sends [action] for the PC activity the session shows. */
    fun command(action: String, positionMs: Long? = null) {
        val (owner, activity) = current ?: return
        HandoffEngine.command(owner, activity.id, action, positionMs)
    }

    private fun update(peers: List<PeerInfo>) {
        val pc = peers.firstOrNull { it.kind == "pc" && it.online }
        val activity = pc?.activities
            ?.filter { it.playback != null }
            ?.sortedByDescending { it.playback?.playing == true }
            ?.firstOrNull { it.playback?.playing == true || System.currentTimeMillis() - Wire.parseTime(it.updatedAt) < RECENT_MS }
        if (pc == null || activity == null) {
            hide()
            return
        }
        current = pc.deviceId to activity
        show(pc, activity)
    }

    private fun show(pc: PeerInfo, activity: Activity) {
        val context = appContext ?: return
        val playback = activity.playback ?: return
        val media = session ?: MediaSession(context, "Baton PC remote").also {
            it.setCallback(Callback)
            session = it
        }

        if (artworkKey != activity.artworkJpegBase64) {
            artworkKey = activity.artworkJpegBase64
            artwork = activity.artworkJpegBase64?.let { runCatching { Base64.decode(it, Base64.DEFAULT) }.getOrNull() }
                ?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
        }
        media.setMetadata(MediaMetadata.Builder()
            .putString(MediaMetadata.METADATA_KEY_TITLE, activity.title)
            .putString(MediaMetadata.METADATA_KEY_ARTIST, activity.subtitle ?: activity.app.name)
            .putString(MediaMetadata.METADATA_KEY_ALBUM, "On ${pc.name}")
            .putLong(MediaMetadata.METADATA_KEY_DURATION, playback.durationMs)
            .apply { artwork?.let { putBitmap(MediaMetadata.METADATA_KEY_ART, it) } }
            .build())
        media.setPlaybackState(PlaybackState.Builder()
            .setState(
                if (playback.playing) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED,
                playback.positionAt(),
                if (playback.playing) playback.rate.toFloat() else 0f,
                android.os.SystemClock.elapsedRealtime()
            )
            .setActions(PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE or
                PlaybackState.ACTION_SEEK_TO or PlaybackState.ACTION_FAST_FORWARD or PlaybackState.ACTION_REWIND or
                PlaybackState.ACTION_SKIP_TO_NEXT or PlaybackState.ACTION_SKIP_TO_PREVIOUS)
            .build())
        media.isActive = true

        // The notification only changes with what is shown, not with every position tick.
        val key = "${activity.id}|${activity.title}|${playback.playing}|${artworkKey?.length}"
        if (key == shownKey) return
        shownKey = key
        ensureChannel(context)
        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle(activity.title)
            .setContentText("${activity.app.name} on ${pc.name}")
            .setLargeIcon(artwork)
            .setOngoing(playback.playing)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setStyle(Notification.MediaStyle().setMediaSession(media.sessionToken).setShowActionsInCompactView(0, 1, 2))
            .addAction(action(context, 1, android.R.drawable.ic_media_rew, "Back 10 s", ACTION_REWIND))
            .addAction(if (playback.playing) {
                action(context, 2, android.R.drawable.ic_media_pause, "Pause", ACTION_TOGGLE)
            } else {
                action(context, 2, android.R.drawable.ic_media_play, "Play", ACTION_TOGGLE)
            })
            .addAction(action(context, 3, android.R.drawable.ic_media_ff, "Forward 10 s", ACTION_FORWARD))
            .addAction(action(context, 4, R.drawable.ic_baton, "Continue here", ACTION_CONTINUE))
            .build()
        runCatching { context.getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification) }
    }

    private fun hide() {
        current = null
        shownKey = null
        session?.let {
            it.isActive = false
            it.release()
        }
        session = null
        appContext?.getSystemService(NotificationManager::class.java)?.cancel(NOTIFICATION_ID)
    }

    private fun action(context: Context, code: Int, icon: Int, label: String, action: String): Notification.Action {
        val intent = PendingIntent.getBroadcast(context, code, Intent(context, PcRemoteReceiver::class.java).setAction(action),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Action.Builder(Icon.createWithResource(context, icon), label, intent).build()
    }

    private fun ensureChannel(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) != null) return
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Playing on your PC", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Controls for what your PC is playing."
            setShowBadge(false)
        })
    }

    /** Continues the shown activity on this phone. */
    internal fun continueHere() {
        val (owner, activity) = current ?: return
        HandoffEngine.pull(owner, activity.id, activity.title)
    }

    private object Callback : MediaSession.Callback() {
        override fun onPlay() = command(MediaActions.PLAY)
        override fun onPause() = command(MediaActions.PAUSE)
        override fun onSeekTo(pos: Long) = command(MediaActions.SEEK, pos)
        override fun onFastForward() = command(MediaActions.SKIP, SKIP_MS)
        override fun onRewind() = command(MediaActions.SKIP, -SKIP_MS)
        override fun onSkipToNext() = command(MediaActions.NEXT)
        override fun onSkipToPrevious() = command(MediaActions.PREVIOUS)
    }

    internal const val ACTION_TOGGLE = "dev.baton.android.remote.TOGGLE"
    internal const val ACTION_REWIND = "dev.baton.android.remote.REWIND"
    internal const val ACTION_FORWARD = "dev.baton.android.remote.FORWARD"
    internal const val ACTION_CONTINUE = "dev.baton.android.remote.CONTINUE"
}

/** The PC remote notification's buttons. */
class PcRemoteReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            PcRemote.ACTION_TOGGLE -> PcRemote.command(MediaActions.TOGGLE)
            PcRemote.ACTION_REWIND -> PcRemote.command(MediaActions.SKIP, -10_000)
            PcRemote.ACTION_FORWARD -> PcRemote.command(MediaActions.SKIP, 10_000)
            PcRemote.ACTION_CONTINUE -> PcRemote.continueHere()
        }
    }
}
