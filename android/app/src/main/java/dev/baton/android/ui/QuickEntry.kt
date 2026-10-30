package dev.baton.android.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.Toast
import dev.baton.android.handoff.ContentLinks
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.handoff.KnownApps
import dev.baton.android.handoff.MediaSessions
import dev.baton.android.link.Link
import dev.baton.android.protocol.Activity as BatonActivity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.HandoffDeliverPayload
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.Playback
import dev.baton.android.protocol.Wire

/** Quick Settings tile: tap sends what's playing to the PC; long-press opens Baton. */
class SendToPcTileService : TileService() {
    override fun onStartListening() {
        qsTile?.apply {
            state = if (Link.isReady) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE
            subtitle = if (Link.isReady) HandoffEngine.local.value.firstOrNull()?.title ?: "Nothing playing" else "Not connected"
            updateTile()
        }
    }

    override fun onClick() {
        if (!Link.isReady) return
        HandoffEngine.sendTo()
        qsTile?.apply {
            subtitle = "Sent"
            updateTile()
        }
    }
}

/**
 * "Continue on PC" in the share sheet. A shared link is exact where a media session's title is
 * not, so a YouTube share combined with the live position from the app's session gives an exact
 * handoff.
 */
class ShareToPcActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val files = sharedFiles()
        if (files.isNotEmpty()) return sendFiles(files)
        val text = intent?.takeIf { it.action == Intent.ACTION_SEND }?.getStringExtra(Intent.EXTRA_TEXT)
        val url = text?.let { Regex("https?://\\S+").find(it)?.value }
        if (url == null || !Link.isReady) {
            Toast.makeText(this, if (url == null) "Baton can continue links only." else "Not connected to your PC.", Toast.LENGTH_SHORT).show()
            finish()
            return
        }

        // The share sheet hides who shared, so the app is inferred from the link itself.
        // Any build of it: ReVanced shares YouTube links too.
        val sourceApp = when {
            url.contains("music.youtube.com") -> KnownApps.fromProvider("youtubemusic")
            ContentLinks.youTubeVideoId(url) != null -> KnownApps.fromProvider("youtube")
            ContentLinks.twitchUrl(url) != null -> KnownApps.fromProvider("twitch")
            url.contains("spotify") -> KnownApps.fromProvider("spotify")
            else -> null
        }
        val session = sourceApp?.let { MediaSessions.controllerForApp(it) }
        val sourcePackage = session?.packageName ?: sourceApp?.androidPackage
        val position = session?.playbackState?.let { state -> with(MediaSessions) { state.currentPosition() } } ?: 0L
        val duration = session?.metadata?.getLong(android.media.MediaMetadata.METADATA_KEY_DURATION) ?: 0L
        session?.transportControls?.pause()

        val title = intent.getStringExtra(Intent.EXTRA_SUBJECT) ?: session?.metadata?.getString(android.media.MediaMetadata.METADATA_KEY_TITLE) ?: url
        val activity = BatonActivity(
            id = "share:${Wire.newId()}",
            deviceId = Link.deviceId,
            kind = if (session != null) ActivityKind.WebMedia else ActivityKind.WebPage,
            title = title,
            app = ActivityApp(sourcePackage?.let { MediaSessions.appName(this, it) } ?: "Share", sourcePackage ?: "share"),
            updatedAt = Wire.time(),
            url = if (ContentLinks.youTubeVideoId(url) != null) ContentLinks.withYouTubeTime(url, position) else url,
            playback = session?.let { Playback(position, duration, playing = false, capturedAt = Wire.time()) }
        )
        HandoffEngine.send(activity)
        Toast.makeText(this, "Continuing on your PC", Toast.LENGTH_SHORT).show()
        finish()
    }

    /** Files (photos, videos, documents) rather than a link: the PC saves and opens them. */
    @Suppress("DEPRECATION")
    private fun sharedFiles(): List<android.net.Uri> = when (intent?.action) {
        Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<android.net.Uri>(Intent.EXTRA_STREAM))
        Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<android.net.Uri>(Intent.EXTRA_STREAM).orEmpty()
        else -> emptyList()
    }

    private fun sendFiles(files: List<android.net.Uri>) {
        if (!Link.isReady) {
            Toast.makeText(this, "Not connected to your PC.", Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        Toast.makeText(this, if (files.size == 1) "Sending to your PC…" else "Sending ${files.size} files to your PC…", Toast.LENGTH_SHORT).show()
        // The copy needs the share's permission, which ends when this screen closes: close after it.
        Thread({
            dev.baton.android.transfer.FileTransfers.send(this, files)
            runOnUiThread { finish() }
        }, "Baton share").start()
    }
}

/**
 * Quick Settings tile: tap continues the PC's activity here, the way the three-finger swipe down
 * does, on any phone and without Shizuku. It names what the PC is doing.
 */
class GetFromPcTileService : TileService() {
    override fun onStartListening() {
        val activity = Link.peers.value.firstOrNull { it.kind == "pc" }?.activities?.firstOrNull()
        qsTile?.apply {
            state = if (Link.isReady && activity != null) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE
            subtitle = when {
                !Link.isReady -> "Not connected"
                activity == null -> "Nothing on your PC"
                else -> activity.title
            }
            updateTile()
        }
    }

    override fun onClick() {
        if (!Link.isReady) return
        // Through an activity of Baton's: the shade closes, and Android lets the app open from the front.
        val intent = Intent(this, PullFromPcActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(android.app.PendingIntent.getActivity(this, 0, intent,
                android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}

/** Invisible: continues the PC's activity here, then gets out of the way. */
class PullFromPcActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        HandoffEngine.pullFromPc()
        finish()
    }
}
