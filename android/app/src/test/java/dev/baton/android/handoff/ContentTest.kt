package dev.baton.android.handoff

import dev.baton.android.protocol.PairingLink
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ContentTest {
    @Test fun twitchStructuredMetadataAndVerifiedChannelLayoutIdentifyCurrentContent() {
        assertEquals("https://www.twitch.tv/videos/123456", ContentLinks.twitchMediaUrl(
            listOf("twitch://video/123456", "https://www.twitch.tv/trymacs"), "Unrelated stream title", "SomeoneElse"))
        assertEquals("https://www.twitch.tv/trymacs", ContentLinks.twitchMediaUrl(emptyList(), "Trymacs", "Streaming Minecraft"))
        assertEquals("https://www.twitch.tv/papaplatte", ContentLinks.twitchMediaUrl(emptyList(), "A stream title", "Papaplatte"))
        assertNull(ContentLinks.twitchMediaUrl(emptyList(), "Trymacs", null))
        assertNull(ContentLinks.twitchMediaUrl(emptyList(), "Minecraft stream title", "Streaming a game"))
    }

    @Test fun twitchLinksRejectOtherHostsAndReservedPages() {
        assertEquals("https://www.twitch.tv/trymacs", ContentLinks.twitchUrl("https://m.twitch.tv/Trymacs"))
        assertEquals("https://www.twitch.tv/trymacs", ContentLinks.twitchUrl("twitch://stream/Trymacs"))
        for (url in listOf("https://twitch.tv.evil.test/trymacs", "https://eviltwitch.tv/trymacs",
            "https://twitch.tv/directory", "https://twitch.tv/search", "https://twitch.tv/trymacs/extra", "twitch://evil/trymacs"))
            assertNull(ContentLinks.twitchUrl(url))
    }
    @Test
    fun resolverRejectsSameTitleWithOtherLength() {
        val results = listOf(
            YouTubeResolver.Candidate("trailer", "The Love Hypothesis", "Studio", YouTubeResolver.parseLength("2:31")),
            YouTubeResolver.Candidate("other", "Something else", "Studio", YouTubeResolver.parseLength("1:44:02"))
        )
        assertNull(YouTubeResolver.pick(results, "The Love Hypothesis", null, 6_240_000))
        assertEquals("trailer", YouTubeResolver.pick(results, "The Love Hypothesis", null, 151_000))
    }

    // Same trimmed search response as the PC's ProtocolTests: a video, a Shorts shelf, a video.
    private val searchResponse = """
        {"contents":{"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[
          {"videoRenderer":{"videoId":"RE3_PGwDwhw","title":{"runs":[{"text":"Gemeinsam 150 Pfund abnehmen und 250.000 ${'$'} gewinnen"}]},
            "ownerText":{"runs":[{"text":"MrBeast"}]},"lengthText":{"simpleText":"43:17"}}},
          {"gridShelfViewModel":{"contents":[{"shortsLockupViewModel":{
            "onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"5S_6Dp5D8Vs"}}},
            "overlayMetadata":{"primaryText":{"content":"Me at the Zoo VS"}}}}]}},
          {"videoRenderer":{"videoId":"jNQXAC9IVRw","title":{"runs":[{"text":"Me at the zoo"}]},
            "ownerText":{"runs":[{"text":"jawed"}]},"lengthText":{"simpleText":"0:19"}}}
        ]}}]}}}
    """.trimIndent()

    private fun parsedSearch() = YouTubeResolver.parse(kotlinx.serialization.json.Json.parseToJsonElement(searchResponse))

    @Test
    fun resolverReadsVideosThenShorts() {
        val results = parsedSearch()
        assertEquals(listOf("RE3_PGwDwhw", "jNQXAC9IVRw", "5S_6Dp5D8Vs"), results.map { it.id })
        assertEquals(listOf(false, false, true), results.map { it.isShort })
        assertEquals("Me at the Zoo VS", results[2].title)
    }

    @Test
    fun resolverMatchesTranslatedTitleOnlyInItsLanguage() {
        val english = listOf(YouTubeResolver.Candidate("RE3_PGwDwhw", "Lose 150 Lbs Together, Win $250,000", "MrBeast", 2_597_000))
        val title = "Gemeinsam 150 Pfund abnehmen und 250.000 $ gewinnen"
        assertNull(YouTubeResolver.pick(english, title, "MrBeast", 2_597_000))
        assertEquals("RE3_PGwDwhw", YouTubeResolver.pick(parsedSearch(), title, "MrBeast", 2_597_000))
    }

    @Test
    fun resolverPicksShortsOnlyForShortPlayback() {
        assertEquals("5S_6Dp5D8Vs", YouTubeResolver.pick(parsedSearch(), "Me at the Zoo VS", null, 42_000))
        assertNull(YouTubeResolver.pick(parsedSearch(), "Me at the Zoo VS", null, 600_000))
    }

    @Test
    fun resolverSearchesLocalLanguageThenEnglish() {
        assertEquals(listOf("de", "en"), YouTubeResolver.searchLanguages("de"))
        assertEquals(listOf("en"), YouTubeResolver.searchLanguages("EN"))
    }

    @Test
    fun parsesLengths() {
        assertEquals(6_205_000L, YouTubeResolver.parseLength("1:43:25"))
        assertEquals(0L, YouTubeResolver.parseLength(""))
    }

    @Test
    fun readsYouTubeLinks() {
        assertEquals("aqz-KE-bpKQ", ContentLinks.youTubeVideoId("https://youtu.be/aqz-KE-bpKQ?si=x"))
        assertEquals("https://www.youtube.com/watch?v=abcdefghijk&t=95s", ContentLinks.withYouTubeTime("https://youtu.be/abcdefghijk", 95_400))
        assertEquals("https://m.youtube.com/watch?v=x", ContentLinks.normalizeUrl("m.youtube.com/watch?v=x"))
        assertNull(ContentLinks.normalizeUrl("search words"))
    }

    @Test
    fun twitchPastBroadcastsKeepTheirSecond() {
        assertEquals("https://www.twitch.tv/videos/2201234567?t=1h2m3s",
            ContentLinks.withTwitchTime("https://www.twitch.tv/videos/2201234567?filter=archives", 3_723_900))
        // A live channel has no time to give.
        assertEquals("https://www.twitch.tv/somechannel", ContentLinks.withTwitchTime("https://www.twitch.tv/somechannel", 60_000))
        assertEquals(true, ContentLinks.isTwitchLive("https://m.twitch.tv/Trymacs"))
        assertEquals(false, ContentLinks.isTwitchLive("https://www.twitch.tv/videos/2201234567"))
        assertEquals(false, ContentLinks.isTwitchLive("https://www.twitch.tv/search"))
    }

    @Test
    fun pagesOpenScrolledToTheirText() {
        assertEquals("https://example.com/a#:~:text=The%20quick%20brown%2C%20fox%2Djumps%20%26%20more",
            ContentLinks.withTextAnchor("https://example.com/a", "The quick brown, fox-jumps & more"))
        assertEquals("https://example.com/a#intro:~:text=Hello%20there", ContentLinks.withTextAnchor("https://example.com/a#intro", " Hello there "))
        assertEquals("https://example.com/a", ContentLinks.withTextAnchor("https://example.com/a", null))
    }

    @Test
    fun linksOpenInTheSiteAppNotTheBrowser() {
        val browsers = setOf("com.android.chrome", "com.sec.android.app.sbrowser")
        assertEquals("tv.twitch.android.app",
            LinkApps.choose(listOf("com.android.chrome", "tv.twitch.android.app"), browsers, "dev.baton.android"))
        assertNull(LinkApps.choose(listOf("com.sec.android.app.sbrowser", "com.android.chrome"), browsers, "dev.baton.android"))
        // A browser that only claims some sites is still a browser; Baton never picks itself.
        assertNull(LinkApps.choose(listOf("org.mozilla.firefox", "dev.baton.android"), browsers, "dev.baton.android"))
    }

    @Test
    fun pairingLinkMatchesPcFormat() {
        // Exactly what the PC's PairingLink.ToString() writes.
        val raw = "baton://pair?h=a47f&n=DESKTOP%20PC&f=1acf&c=683947&e=192.168.178.125%3A7838"
        val link = PairingLink.parse(raw)!!
        assertEquals("DESKTOP PC", link.pcName)
        assertEquals(7838, link.endpoints.single().port)
        assertEquals(link, PairingLink.parse(link.toUri()))
    }

    @Test
    fun samsungInternetAddressesAreCleanedAndHostOnlyIsSpotted() {
        // Samsung Internet's bar: a left-to-right mark, then only the site until it is edited.
        val shown = ContentLinks.normalizeUrl("‎gamepro.de")
        assertEquals("https://gamepro.de", shown)
        assertEquals(true, ContentLinks.isHostOnly(shown!!))
        assertEquals(false, ContentLinks.isHostOnly("https://www.gamepro.de/artikel/ps-plus,3459717.html"))
        assertEquals(false, ContentLinks.isHostOnly("https://example.com/?q=1"))
    }
}
