package dev.baton.android.handoff

import android.os.Handler
import android.os.Looper
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityContent
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.Wire

/**
 * The page each browser last showed, as read from its URL bar by [BatonAccessibilityService], and
 * what Maps or WhatsApp showed ([AppPages]): a link, or for a chat no link opens, its name.
 */
object BrowserPages {
    data class Page(val packageName: String, val url: String?, val title: String?, val seenAt: Long, val content: ActivityContent? = null)

    private const val PAGE_LIFETIME_MS = 30 * 60 * 1000L
    private val handler = Handler(Looper.getMainLooper())
    private val pages = mutableMapOf<String, Page>()

    @Volatile
    var foregroundPackage: String? = null
        private set

    /** Called on the main thread when a page changes. */
    var onChanged: (() -> Unit)? = null

    fun onForeground(packageName: String) {
        if (foregroundPackage == packageName) return
        foregroundPackage = packageName
        // Opening a build of YouTube (or another known app) is using it, playing or not.
        MediaSessions.noteForeground(packageName)
        handler.post { onChanged?.invoke() }
    }

    fun onPage(packageName: String, url: String?, title: String?, content: ActivityContent? = null) = handler.post {
        val previous = pages[packageName]
        if (previous != null && previous.url == url && previous.title == title && previous.content == content) return@post
        pages[packageName] = Page(packageName, url, title, System.currentTimeMillis(), content)
        onChanged?.invoke()
    }

    fun page(packageName: String): Page? =
        pages[packageName]?.takeIf { System.currentTimeMillis() - it.seenAt < PAGE_LIFETIME_MS }

    /** The newest page as an activity, unless that browser is already covered by a media session. */
    fun activity(deviceId: String, appName: (String) -> String, coveredPackages: Set<String>): Activity? {
        val page = pages.values
            .filter { it.packageName !in coveredPackages && System.currentTimeMillis() - it.seenAt < PAGE_LIFETIME_MS }
            .maxByOrNull { it.seenAt } ?: return null
        return Activity(
            id = "page:${page.packageName}",
            deviceId = deviceId,
            kind = ActivityKind.WebPage,
            title = page.title?.takeIf { it.isNotBlank() } ?: page.url?.let(::readable) ?: "",
            app = ActivityApp(appName(page.packageName), page.packageName),
            updatedAt = Wire.time(page.seenAt),
            subtitle = page.url?.let(::hostOf) ?: appName(page.packageName),
            url = page.url,
            content = page.content
        )
    }

    private fun hostOf(url: String): String? = runCatching { java.net.URI(url).host?.removePrefix("www.") }.getOrNull()

    private fun readable(url: String): String {
        val uri = runCatching { java.net.URI(url) }.getOrNull() ?: return url
        val path = uri.path?.trim('/')?.split('/')?.lastOrNull { it.isNotBlank() }
            ?.replace('-', ' ')?.replace('_', ' ')
        return path?.replaceFirstChar { it.uppercase() } ?: (uri.host?.removePrefix("www.") ?: url)
    }
}
