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
}
