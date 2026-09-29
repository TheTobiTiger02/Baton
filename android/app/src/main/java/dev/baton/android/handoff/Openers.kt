package dev.baton.android.handoff

import android.app.SearchManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.MediaStore
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ChoiceKinds
import dev.baton.android.protocol.HandoffChoice
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.HandoffStatus
import dev.baton.android.player.PlayerActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

data class OpenResult(val status: HandoffStatus, val detail: String? = null)

/**
 * Continues an activity another device handed to this phone: picks the best link or intent for
 * the content, starts it, then nudges the app's media session to the right second.
 */
object Openers {
    private val reconcileScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /**
     * Opens [activity] the way [choice] says: a website, a specific app (given the link when there
     * is one), or (default) the content's own app at the same second.
     */
    suspend fun open(context: Context, activity: Activity, choice: HandoffChoice?): OpenResult {
        when (choice?.kind) {
            ChoiceKinds.WEB -> (choice.url ?: activity.url)?.let { url ->
                val target = if (choice.url == null) ContentLinks.withTextAnchor(url, activity.textAnchor) else url
                return launchResult(Launcher.start(context, Intent(Intent.ACTION_VIEW, Uri.parse(target)), activity.title))
            }
            ChoiceKinds.APP -> choice.appId?.let { app ->
                // The content's own app (or a build of it, like ReVanced) keeps the usual path,
                // which knows the position, pinned to the chosen build.
                val usual = KnownApps.fromProvider(activity.content?.provider)
                if (usual == null || KnownApps.fromPackage(app) != usual) return openIn(context, activity, app, choice.label)
                return open(context, activity, pinned = app)
            }
        }
        return open(context, activity)
    }

    private fun openIn(context: Context, activity: Activity, app: String, label: String): OpenResult {
        val url = activity.url
        if (url != null) {
            val positionMs = activity.playback?.positionAt() ?: 0L
            // A browser scrolls the page to where it was read; an app gets the plain link.
            val target = when {
                ContentLinks.youTubeVideoId(url) != null -> ContentLinks.withYouTubeTime(url, positionMs)
                KnownApps.fromPackage(app)?.isBrowser == true || LinkApps.isBrowser(context, app) -> ContentLinks.withTextAnchor(url, activity.textAnchor)
                else -> url
            }
            val view = Intent(Intent.ACTION_VIEW, Uri.parse(target)).setPackage(app)
            if (view.resolveActivity(context.packageManager) != null) {
                return launchResult(launch(context, view, activity, app, positionMs.takeIf { activity.playback != null }))
            }
        }
        val launch = context.packageManager.getLaunchIntentForPackage(app)
            ?: return OpenResult(HandoffStatus.Failed, "$label isn't installed on this phone.")
        if (!Launcher.start(context, launch, activity.title)) return launchResult(false)
        return if (url != null) OpenResult(HandoffStatus.Fallback, "Opened $label; it can't be given the page itself.") else OpenResult(HandoffStatus.Opened)
    }

    suspend fun open(context: Context, activity: Activity, pinned: String? = null): OpenResult {
        if (activity.kind == ActivityKind.LocalMedia && activity.file != null) {
            // The file stays on the PC; Baton's player streams it from there.
            return launchResult(Launcher.start(context, PlayerActivity.intent(context, activity), activity.title))
        }
        if (activity.kind == ActivityKind.LocalMedia || activity.kind == ActivityKind.WindowStream) {
            return OpenResult(HandoffStatus.Failed, "This can only be continued as a stream, and the PC didn't offer one.")
        }

        val positionMs = activity.playback?.positionAt() ?: 0L
        activity.url?.let { return openUrl(context, activity, it, positionMs, pinned) }

        val content = activity.content
        val artist = activity.subtitle
        val query = content?.query ?: listOfNotNull(activity.title, artist).joinToString(" ")
        return when (content?.provider) {
            "youtube", "youtubemusic", "web", "unknown" -> openYouTube(context, activity, content.provider, positionMs, pinned)
            "spotify" -> openMusic(context, activity, KnownApps.fromProvider("spotify")!!, query, positionMs, content.id, pinned)
            "netflix", "disney", "prime" -> openVideoService(context, activity, KnownApps.fromProvider(content.provider)!!)
            "stremio" -> openStremio(context, activity, content.id)
            else -> {
                val intent = playFromSearch(query, activity.title, artist)
                if (Launcher.start(context, intent, activity.title)) {
                    OpenResult(HandoffStatus.Fallback, "Asked your music app to play ${activity.title}.")
                } else {
                    OpenResult(HandoffStatus.Fallback, "Tap the notification to continue.")
                }
            }
        }
    }

    private fun openUrl(context: Context, activity: Activity, url: String, positionMs: Long, pinned: String? = null): OpenResult {
        val videoId = ContentLinks.youTubeVideoId(url)
        val target = if (videoId != null) ContentLinks.withYouTubeTime(url, positionMs) else ContentLinks.withTwitchTime(url, positionMs)
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse(target))
        val linkApp = if (videoId == null) pinned ?: LinkApps.appFor(context, target) else null
        val appPackage = when {
            videoId == null -> linkApp
            url.contains("music.youtube.com") -> KnownApps.fromProvider("youtubemusic")?.let { installedPackage(context, it, pinned) }
            else -> KnownApps.fromProvider("youtube")?.let { installedPackage(context, it, pinned) }
        }
        if (appPackage != null && isUsable(context, appPackage)) intent.setPackage(appPackage)
        // A page that opens in the browser scrolls to where it was read on the other device.
        if (videoId == null && linkApp == null) intent.data = Uri.parse(ContentLinks.withTextAnchor(target, activity.textAnchor))
        // Also for YouTube links with a time: an app already showing that video (paused) only
        // moves there and stays paused, so the session is made to play at the right second.
        // Live streams have no second to move to.
        return launchResult(launch(context, intent, activity, appPackage, positionMs.takeIf { activity.playback?.durationMs?.let { it > 0 } == true }))
    }

    private suspend fun openYouTube(context: Context, activity: Activity, provider: String, positionMs: Long, pinned: String? = null): OpenResult {
        val music = provider == "youtubemusic"
        val id = activity.content?.id ?: YouTubeResolver.findVideoId(activity.title, activity.subtitle, activity.playback?.durationMs ?: 0L)
        if (id == null) {
            return if (provider == "web") {
                OpenResult(HandoffStatus.Failed,
                    "Baton couldn't tell which page this is. Install the Baton browser extension on the PC for exact web handoff.")
            } else if (provider == "unknown") {
                OpenResult(HandoffStatus.Failed, "Baton can't find ${activity.title} on this phone. ${activity.app.name} isn't available here.")
            } else {
                val search = Uri.parse("https://www.youtube.com/results?search_query=" + Uri.encode(activity.title))
                if (Launcher.start(context, Intent(Intent.ACTION_VIEW, search), activity.title))
                    OpenResult(HandoffStatus.Fallback, "Couldn't find the exact video, so Baton opened a search.")
                else launchResult(false)
            }
        }

        val appPackage = installedPackage(context, KnownApps.fromProvider(if (music) "youtubemusic" else "youtube")!!, pinned)
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse(ContentLinks.youTubeWatch(id, positionMs, music)))
        if (isUsable(context, appPackage)) intent.setPackage(appPackage)
        return launchResult(launch(context, intent, activity, appPackage, positionMs.takeIf { activity.playback != null }))
    }

    private fun openMusic(context: Context, activity: Activity, app: KnownApp, query: String, positionMs: Long, uri: String?, pinned: String? = null): OpenResult {
        val appPackage = installedPackage(context, app, pinned)
        if (!isUsable(context, appPackage)) {
            return OpenResult(HandoffStatus.Failed, "${app.displayName} isn't installed on this phone.")
        }

        val intent = if (uri != null) {
            Intent(Intent.ACTION_VIEW, Uri.parse(uri)).setPackage(appPackage)
        } else {
            playFromSearch(query, activity.title, activity.subtitle).setPackage(appPackage)
        }
        return launchResult(launch(context, intent, activity, appPackage, positionMs))
    }

    /**
     * Stremio shows the title (and episode, when known) and resumes from the progress it keeps
     * with the account. Baton passes no stream: what plays is the user's own choice in Stremio.
     */
    private fun openStremio(context: Context, activity: Activity, id: String?): OpenResult {
        val app = KnownApps.fromProvider("stremio")!!
        if (!isUsable(context, app.androidPackage)) {
            return OpenResult(HandoffStatus.Failed, "Stremio isn't installed on this phone. Choose \"Stream the window\" instead.")
        }
        val link = if (id != null) "stremio:///detail/$id" else "stremio:///search?search=" + Uri.encode(activity.title)
        if (!Launcher.start(context, Intent(Intent.ACTION_VIEW, Uri.parse(link)).setPackage(app.androidPackage), activity.title)) return launchResult(false)
        return OpenResult(HandoffStatus.Fallback, "Opened ${activity.title} in Stremio: it continues where your account says you stopped.")
    }

    private fun openVideoService(context: Context, activity: Activity, app: KnownApp): OpenResult {
        val launch = context.packageManager.getLaunchIntentForPackage(app.androidPackage)
            ?: return OpenResult(HandoffStatus.Failed, "${app.displayName} isn't installed on this phone.")
        if (!Launcher.start(context, launch, activity.title)) return launchResult(false)
        return OpenResult(HandoffStatus.Fallback,
            "Opened ${app.displayName}. Pick ${activity.title} and press Resume: ${app.displayName} remembers where you stopped.")
    }

    /** Starts the intent, then (when [positionMs] is set) moves the resulting session there. */
    internal fun launchResult(started: Boolean) = if (started) OpenResult(HandoffStatus.Opened,
        "App opened; playback and exact position are not confirmed.")
        else OpenResult(HandoffStatus.Fallback, "Tap the notification on this phone to continue; the app hasn't opened yet.")

    private fun launch(context: Context, intent: Intent, activity: Activity, appPackage: String?, positionMs: Long?): Boolean {
        if (!Launcher.start(context, intent, activity.title)) return false
        if (positionMs == null) return true
        reconcileScope.launch {
            MediaSessions.reconcile(
                // By title: the app may still hold the video it showed before this one loads.
                matches = { controller ->
                    val title = controller.metadata?.getString(android.media.MediaMetadata.METADATA_KEY_TITLE)
                    titlesMatch(title, activity.title) || (title == null && controller.packageName == appPackage)
                },
                positionMs = positionMs,
                play = true
            )
        }
        return true
    }

    private fun playFromSearch(query: String, title: String?, artist: String?): Intent =
        Intent(MediaStore.INTENT_ACTION_MEDIA_PLAY_FROM_SEARCH)
            .putExtra(MediaStore.EXTRA_MEDIA_FOCUS, "vnd.android.cursor.item/audio")
            .putExtra(SearchManager.QUERY, query)
            .apply {
                title?.let { putExtra(MediaStore.EXTRA_MEDIA_TITLE, it) }
                artist?.let { putExtra(MediaStore.EXTRA_MEDIA_ARTIST, it) }
            }

    /**
     * The build of [app] to open: [pinned] when the user chose one, else the build they used last
     * (see [KnownApps.packageFor]); never a disabled one.
     */
    private fun installedPackage(context: Context, app: KnownApp, pinned: String? = null): String =
        pinned?.takeIf { it in app.packages && isUsable(context, it) }
            ?: KnownApps.packageFor(app, { isUsable(context, it) }, MediaSessions.lastUsed(app.provider))

    /** Installed and not disabled: a disabled YouTube is still "installed" but can't open anything. */
    fun isUsable(context: Context, packageName: String): Boolean = try {
        context.packageManager.getApplicationInfo(packageName, 0).enabled
    } catch (_: PackageManager.NameNotFoundException) {
        false
    }

    fun isInstalled(context: Context, packageName: String): Boolean = try {
        context.packageManager.getPackageInfo(packageName, 0)
        true
    } catch (_: PackageManager.NameNotFoundException) {
        false
    }

    fun titlesMatch(a: String?, b: String?): Boolean {
        val left = a.orEmpty().lowercase().filter { it.isLetterOrDigit() }
        val right = b.orEmpty().lowercase().filter { it.isLetterOrDigit() }
        return left.isNotEmpty() && right.isNotEmpty() && (left.contains(right) || right.contains(left))
    }
}
