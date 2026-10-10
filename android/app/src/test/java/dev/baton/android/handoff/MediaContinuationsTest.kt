package dev.baton.android.handoff

import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.Wire
import org.junit.Assert.*
import org.junit.Test

class MediaContinuationsTest {
    private fun activity(url: String? = null, token: String? = null, app: String = "tv.twitch.android.app") =
        Activity("media:$app", "phone", ActivityKind.AppMedia, "Channel", ActivityApp("App", app),
            "2026-10-10T10:00:00Z", url = url, continuationId = token)

    @Test fun currentChannelWinsAndMissingMetadataUsesExactLastSentUrl() {
        val bindings = MediaContinuations()
        val sent = "https://www.twitch.tv/Trymacs?ref=sent"
        bindings.bind(activity(sent, "return"), "pc", "tv.twitch.android.app")
        val missing = bindings.enrich(activity(), "pc")
        assertEquals(sent, missing.url)
        assertEquals("return", missing.continuationId)
        assertEquals("https://www.twitch.tv/papaplatte", bindings.enrich(activity("https://www.twitch.tv/papaplatte"), "pc").url)
    }

    @Test fun bindsOnlyToActuallyOpenedAppAndHost() {
        val bindings = MediaContinuations()
        bindings.bind(activity("https://www.twitch.tv/trymacs", "return"), "pc", "com.sec.android.app.sbrowser")
        assertNull(bindings.enrich(activity(), "pc").continuationId)
        assertEquals("return", bindings.enrich(activity(app = "com.sec.android.app.sbrowser"), "pc").continuationId)
        assertNull(bindings.enrich(activity(app = "com.sec.android.app.sbrowser"), "another-pc").continuationId)
    }

    @Test fun nextHandoffReplacesContextAndLegacyHandoffClearsIt() {
        val bindings = MediaContinuations()
        bindings.bind(activity("https://www.twitch.tv/trymacs", "old"), "pc", "tv.twitch.android.app")
        bindings.bind(activity("https://www.twitch.tv/papaplatte", "new"), "pc", "tv.twitch.android.app")
        assertEquals("new", bindings.enrich(activity(), "pc").continuationId)
        bindings.bind(activity("https://www.twitch.tv/trymacs"), "pc", "tv.twitch.android.app")
        assertNull(bindings.enrich(activity(), "pc").continuationId)
    }

    @Test fun ordinaryMediaDoesNotReuseOldContentAndBindingsAreBoundedAndSessionOnly() {
        val bindings = MediaContinuations()
        bindings.bind(activity("https://www.youtube.com/watch?v=abcdefg", "video", "com.google.android.youtube"), "pc", "com.google.android.youtube")
        assertNull(bindings.enrich(activity(app = "com.google.android.youtube"), "pc").url)
        assertEquals("video", bindings.enrich(activity(app = "com.google.android.youtube"), "pc").continuationId)
        for (i in 0..40) bindings.bind(activity("https://example.com/$i", "r$i", "app$i"), "pc", "app$i")
        assertNull(bindings.enrich(activity(app = "app0"), "pc").continuationId)
        assertEquals("r40", bindings.enrich(activity(app = "app40"), "pc").continuationId)
        bindings.clear()
        assertNull(bindings.enrich(activity(app = "app40"), "pc").continuationId)
    }

    @Test fun optionalContinuationRemainsWireCompatible() {
        val legacy = Wire.json.encodeToString(Activity.serializer(), activity())
        assertFalse(legacy.contains("continuationId"))
        assertNull(Wire.json.decodeFromString(Activity.serializer(), legacy).continuationId)
        val modern = Wire.json.encodeToString(Activity.serializer(), activity(token = "return"))
        assertEquals("return", Wire.json.decodeFromString(Activity.serializer(), modern).continuationId)
    }
}
