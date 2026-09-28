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
