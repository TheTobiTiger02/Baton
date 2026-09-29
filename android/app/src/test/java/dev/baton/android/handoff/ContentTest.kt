package dev.baton.android.handoff

import dev.baton.android.protocol.PairingLink
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ContentTest {
    @Test
    fun resolverRejectsSameTitleWithOtherLength() {
        val results = listOf(
            YouTubeResolver.Candidate("trailer", "The Love Hypothesis", "Studio", YouTubeResolver.parseLength("2:31")),
            YouTubeResolver.Candidate("other", "Something else", "Studio", YouTubeResolver.parseLength("1:44:02"))
        )
        assertNull(YouTubeResolver.pick(results, "The Love Hypothesis", null, 6_240_000))
        assertEquals("trailer", YouTubeResolver.pick(results, "The Love Hypothesis", null, 151_000))
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
