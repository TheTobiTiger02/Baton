package dev.baton.android.player

import android.app.Activity as AndroidActivity
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.os.Bundle
import android.view.Gravity
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.VideoSize
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.link.Link
import dev.baton.android.link.PinnedTls
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.MediaActions
import dev.baton.android.protocol.Playback
import dev.baton.android.protocol.Wire

/**
 * Plays a file that lives on the PC, streamed over the pinned connection, starting where the PC
 * stopped. "Send back to PC" hands it back at the current second.
 */
@OptIn(UnstableApi::class)
class PlayerActivity : AndroidActivity() {
    private var player: ExoPlayer? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val activity = Wire.json.decodeFromString(Activity.serializer(), intent.getStringExtra(EXTRA_ACTIVITY) ?: return finish())
        val file = activity.file ?: return finish()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        val http = OkHttpDataSource.Factory(PinnedTls.newClient(Link.pcFingerprint))
        val exo = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(http))
            .build()
        player = exo
        exo.setMediaItem(MediaItem.Builder()
            .setUri("https://${Link.pcAddress}:$FILE_PORT/files/${file.fileId}")
            .setMimeType(file.mime.takeIf { it != "application/octet-stream" })
            .build(), activity.playback?.positionAt() ?: 0L)
        exo.playWhenReady = true
        exo.prepare()
        exo.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(isPlaying: Boolean) = NowPlaying.changed()
            override fun onPositionDiscontinuity(old: Player.PositionInfo, new: Player.PositionInfo, reason: Int) = NowPlaying.changed()

            override fun onVideoSizeChanged(size: VideoSize) {
                if (size.width > 0 && size.height > 0) {
                    requestedOrientation = if (size.width >= size.height) {
                        ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    } else {
                        ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                    }
                }
            }
        })
        NowPlaying.start(activity, exo)

        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        root.addView(PlayerView(this).apply {
            this.player = exo
            setShowNextButton(false)
            setShowPreviousButton(false)
            controllerShowTimeoutMs = 3_000
        }, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(Button(this).apply {
            text = "Send back to PC"
            isAllCaps = false
            setOnClickListener {
                exo.pause()
                NowPlaying.snapshot()?.let(HandoffEngine::send)
                finish()
            }
        }, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.END).apply {
            val margin = (16 * resources.displayMetrics.density).toInt()
            setMargins(0, margin, margin, 0)
        })
        setContentView(root)
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun onDestroy() {
        NowPlaying.stop(player)
        player?.release()
        player = null
        super.onDestroy()
    }

    companion object {
        private const val EXTRA_ACTIVITY = "activity"
        private const val FILE_PORT = 7838

        fun intent(context: Context, activity: Activity): Intent =
            Intent(context, PlayerActivity::class.java)
                .putExtra(EXTRA_ACTIVITY, Wire.json.encodeToString(Activity.serializer(), activity))
    }
}

/** The file Baton's own player is playing, offered like any other activity on this phone. */
object NowPlaying {
    @Volatile private var activity: Activity? = null
    @Volatile private var player: Player? = null

    fun start(activity: Activity, player: Player) {
        this.activity = activity.copy(deviceId = Link.deviceId)
        this.player = player
        changed()
    }

    fun stop(player: Player?) {
        if (this.player === player) {
            this.player = null
            this.activity = null
            changed()
        }
    }

    fun changed() = HandoffEngine.refresh()

    /** The activity with the player's position right now. Main thread. */
    fun snapshot(): Activity? {
        val current = activity ?: return null
        val active = player ?: return null
        val now = System.currentTimeMillis()
        return current.copy(
            updatedAt = Wire.time(now),
            playback = Playback(
                positionMs = active.currentPosition.coerceAtLeast(0),
                durationMs = active.duration.takeIf { it > 0 } ?: (current.playback?.durationMs ?: 0),
                playing = active.isPlaying,
                capturedAt = Wire.time(now)
            )
        )
    }

    fun pause() {
        player?.pause()
    }

    /** A remote command from another device. Main thread. */
    fun command(action: String, positionMs: Long?, volume: Double?): Boolean {
        val active = player ?: return false
        when (action) {
            MediaActions.PLAY -> active.play()
            MediaActions.PAUSE -> active.pause()
            MediaActions.TOGGLE -> if (active.isPlaying) active.pause() else active.play()
            MediaActions.SEEK -> active.seekTo(positionMs ?: return false)
            MediaActions.SKIP -> active.seekTo((active.currentPosition + (positionMs ?: return false)).coerceAtLeast(0))
            MediaActions.VOLUME -> active.volume = (volume ?: return false).toFloat().coerceIn(0f, 1f)
            else -> return false
        }
        changed()
        return true
    }
}
