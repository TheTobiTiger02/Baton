package dev.baton.android.handoff

/**
 * Twin of `Baton.Host.Media.KnownApps`: the media apps Baton knows on both platforms, linked by
 * [provider].
 */
data class KnownApp(
    val provider: String,
    val displayName: String,
    val androidPackage: String,
    val isBrowser: Boolean = false,
    val isVideoService: Boolean = false,
    val playsFromSearch: Boolean = false,
    /** Patched builds of the app (ReVanced and the like), preferred over [androidPackage] when installed. */
    val variants: List<String> = emptyList()
) {
    val packages: List<String> get() = variants + androidPackage
}

object KnownApps {
    val all = listOf(
        KnownApp("spotify", "Spotify", "com.spotify.music", playsFromSearch = true),
        KnownApp("netflix", "Netflix", "com.netflix.mediaclient", isVideoService = true),
        KnownApp("disney", "Disney+", "com.disney.disneyplus", isVideoService = true),
        KnownApp("prime", "Prime Video", "com.amazon.avod.thirdpartyclient", isVideoService = true),
        KnownApp("youtubemusic", "YouTube Music", "com.google.android.apps.youtube.music", playsFromSearch = true,
            variants = listOf("app.revanced.android.apps.youtube.music", "app.rvx.android.apps.youtube.music", "com.vanced.android.apps.youtube.music")),
        KnownApp("youtube", "YouTube", "com.google.android.youtube",
            variants = listOf("app.revanced.android.youtube", "app.rvx.android.youtube", "anddea.youtube", "com.vanced.android.youtube")),
        KnownApp("vlc", "VLC", "org.videolan.vlc"),
        KnownApp("twitch", "Twitch", "tv.twitch.android.app"),
        KnownApp("stremio", "Stremio", "com.stremio.one"),
        KnownApp("chrome", "Chrome", "com.android.chrome", isBrowser = true),
        KnownApp("chrome", "Chrome Beta", "com.chrome.beta", isBrowser = true),
        KnownApp("samsung", "Samsung Internet", "com.sec.android.app.sbrowser", isBrowser = true),
        KnownApp("edge", "Edge", "com.microsoft.emmx", isBrowser = true),
        KnownApp("opera", "Opera", "com.opera.browser", isBrowser = true),
        KnownApp("firefox", "Firefox", "org.mozilla.firefox", isBrowser = true),
        KnownApp("brave", "Brave", "com.brave.browser", isBrowser = true)
    )

    /** The app a package is, including patched builds: any "…youtube…" package is YouTube (or YouTube Music). */
    fun fromPackage(packageName: String?): KnownApp? {
        if (packageName == null) return null
        all.firstOrNull { packageName in it.packages }?.let { return it }
        val lower = packageName.lowercase()
        return when {
            "youtube" in lower && "music" in lower -> fromProvider("youtubemusic")
            "youtube" in lower -> fromProvider("youtube")
            else -> null
        }
    }

    /**
     * The package to open [app] in, among the usable builds: the one used last ([lastUsed]), else
     * a patched build (someone who installed ReVanced uses it; the store keeps updating the stock
     * app anyway, so update times say nothing), else the original.
     */
    fun packageFor(app: KnownApp, isUsable: (String) -> Boolean, lastUsed: String?): String {
        val usable = app.packages.filter(isUsable)
        return lastUsed?.takeIf { it in usable }
            ?: usable.firstOrNull()
            ?: app.androidPackage
    }

    fun fromProvider(provider: String?): KnownApp? =
        all.firstOrNull { it.provider.equals(provider, ignoreCase = true) && !it.isBrowser }

    /** The URL bar of each supported browser, as the accessibility tree names it. */
    val urlBarIds = mapOf(
        "com.android.chrome" to "com.android.chrome:id/url_bar",
        "com.chrome.beta" to "com.chrome.beta:id/url_bar",
        "com.microsoft.emmx" to "com.microsoft.emmx:id/url_bar",
        "com.brave.browser" to "com.brave.browser:id/url_bar",
        "com.sec.android.app.sbrowser" to "com.sec.android.app.sbrowser:id/location_bar_edit_text",
        "org.mozilla.firefox" to "org.mozilla.firefox:id/mozac_browser_toolbar_url_view",
        "com.opera.browser" to "com.opera.browser:id/url_field"
    )
}

/** Building and reading the links of services Baton understands. Twin of `ContentLinks` on the PC. */
object ContentLinks {
    private val videoIdQuery = Regex("[?&]v=([A-Za-z0-9_-]{6,})")
    private val videoIdPath = Regex("^/(?:shorts|live|embed)/([A-Za-z0-9_-]{6,})")

    fun youTubeWatch(videoId: String, positionMs: Long, music: Boolean = false): String {
        val host = if (music) "music.youtube.com" else "www.youtube.com"
        val seconds = positionMs / 1000
        return if (seconds > 0) "https://$host/watch?v=$videoId&t=${seconds}s" else "https://$host/watch?v=$videoId"
    }

    fun youTubeVideoId(url: String?): String? {
        val uri = runCatching { java.net.URI(url ?: return null) }.getOrNull() ?: return null
        val host = uri.host?.lowercase() ?: return null
        if (host == "youtu.be") return uri.path.trim('/').split('/').firstOrNull()?.takeIf { it.isNotBlank() }
        if (!host.endsWith("youtube.com")) return null
        videoIdQuery.find("?" + (uri.rawQuery ?: ""))?.let { return it.groupValues[1] }
        return videoIdPath.find(uri.path ?: "")?.groupValues?.get(1)
    }

    private val twitchVideo = Regex("""^https?://(?:www\.|m\.)?twitch\.tv/videos/(\d+)""", RegexOption.IGNORE_CASE)

    /** A Twitch past broadcast at the second it was left: `?t=1h2m3s`. Live channels have no time. */
    fun withTwitchTime(url: String, positionMs: Long): String {
        val id = twitchVideo.find(url)?.groupValues?.get(1) ?: return url
        val seconds = positionMs / 1000
        return "https://www.twitch.tv/videos/$id?t=${seconds / 3600}h${seconds / 60 % 60}m${seconds % 60}s"
    }

    /**
     * The page scrolled to the text a browser should show at the top: a text fragment
     * (`#:~:text=`), which Chrome, Samsung Internet, Edge and Firefox 131+ scroll to by
     * themselves. The page's own `#fragment` stays in front. The PC's twin is ContentLinks.WithTextAnchor.
     */
    fun withTextAnchor(url: String, anchor: String?): String {
        if (anchor.isNullOrBlank() || ":~:" in url) return url
        val text = java.net.URLEncoder.encode(anchor.trim(), "UTF-8").replace("+", "%20")
            .replace("-", "%2D").replace(",", "%2C").replace("&", "%26")
        return if ('#' in url) "$url:~:text=$text" else "$url#:~:text=$text"
    }

    fun withYouTubeTime(url: String, positionMs: Long): String {
        val id = youTubeVideoId(url) ?: return url
        return youTubeWatch(id, positionMs, url.contains("music.youtube.com", ignoreCase = true))
    }

    /** Direction marks and zero-width characters: Samsung Internet puts one before the host. */
    private val invisible = Regex("[\\u200B-\\u200F\\u202A-\\u202E\\u2066-\\u2069\\uFEFF]")

    /** Browsers show URLs without a scheme; put one back so the PC can open it. */
    fun normalizeUrl(text: String): String? {
        val trimmed = text.replace(invisible, "").trim()
        if (trimmed.isBlank() || trimmed.contains(' ') || !trimmed.contains('.')) return null
        return if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) trimmed else "https://$trimmed"
    }

    /**
     * Only a site, no page: what Samsung Internet's bar shows while it isn't being edited. Opening
     * that on the PC would land on the home page, so the full address is read first.
     */
    fun isHostOnly(url: String): Boolean {
        val uri = runCatching { java.net.URI(url) }.getOrNull() ?: return false
        return uri.host != null && (uri.rawPath.isNullOrEmpty() || uri.rawPath == "/") && uri.rawQuery == null
    }
}

/**
 * Which app a link opens in: an installed app that says it handles it (Twitch, X, Reddit…), not
 * the browser. Android gives unverified app links to the browser, so Baton picks the app itself.
 */
object LinkApps {
    /** The app among [handlers] that isn't one of the [browsers] (or Baton), or null for the browser. */
    fun choose(handlers: List<String>, browsers: Set<String>, self: String): String? =
        handlers.firstOrNull { it !in browsers && it != self && KnownApps.fromPackage(it)?.isBrowser != true }

    /** Whether [packageName] opens any web page, i.e. is a browser. */
    fun isBrowser(context: android.content.Context, packageName: String): Boolean = runCatching {
        context.packageManager.queryIntentActivities(
            android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse("https://example.com/"))
                .addCategory(android.content.Intent.CATEGORY_BROWSABLE).setPackage(packageName),
            android.content.pm.PackageManager.MATCH_ALL
        ).isNotEmpty()
    }.getOrDefault(false)

    fun appFor(context: android.content.Context, url: String): String? {
        val packages = context.packageManager
        fun handlers(target: String) = runCatching {
            packages.queryIntentActivities(
                android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(target))
                    .addCategory(android.content.Intent.CATEGORY_BROWSABLE),
                android.content.pm.PackageManager.MATCH_ALL
            ).map { it.activityInfo.packageName }.distinct()
        }.getOrDefault(emptyList())
        // What handles any web page is a browser; what handles only this site is its app. Only
        // apps with a home screen icon: system services (Play services) also claim some links.
        val browsers = handlers("https://example.com/").toSet()
        return choose(handlers(url).filter { packages.getLaunchIntentForPackage(it) != null }, browsers, context.packageName)
    }
}
