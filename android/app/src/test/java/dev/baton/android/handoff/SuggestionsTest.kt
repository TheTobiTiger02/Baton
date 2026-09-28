package dev.baton.android.handoff

import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.Playback
import dev.baton.android.protocol.PresenceState
import dev.baton.android.protocol.Wire
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Same cases as the PC's ProtocolTests.Suggestion* tests. */
class SuggestionsTest {
    private val now = 1_800_000_000_000L

    private fun activity(id: String, ageMinutes: Long, playing: Boolean? = null) = Activity(
        id = id, deviceId = "pc", kind = ActivityKind.AppMedia, title = id, app = ActivityApp("App", "app"),
        updatedAt = Wire.time(now - ageMinutes * 60_000),
        playback = playing?.let { Playback(0, 60_000, it, 1.0, Wire.time(now)) }
    )

    @Test
    fun prefersPlayingThenRecentAndSkipsDevicesInUse() {
        val old = activity("old", 20)
        val recent = activity("recent", 1)
        val older = activity("older", 3)
        val playing = activity("playing", 10, playing = true)

        assertEquals("playing", Suggestions.pick(listOf(recent, playing), PresenceState.Active, now)?.id)
        assertEquals("recent", Suggestions.pick(listOf(old, older, recent), PresenceState.Locked, now)?.id)
        assertNull(Suggestions.pick(listOf(recent), PresenceState.Active, now))
        assertNull(Suggestions.pick(listOf(old), PresenceState.Idle, now))
    }

    @Test
    fun offersOncePerWindow() {
        val suggestions = Suggestions()
        val page = activity("page", 0)
        assertTrue(suggestions.tryOffer("pc", page, now))
        assertFalse(suggestions.tryOffer("pc", page, now + 10 * 60_000))
        assertTrue(suggestions.tryOffer("other", page, now + 10 * 60_000))
        assertTrue(suggestions.tryOffer("pc", page, now + Suggestions.REPEAT_AFTER_MS))
    }

    @Test
    fun implausibleDurationsBecomeUnknown() {
        assertEquals(0L, Playback.plausibleDuration(Long.MAX_VALUE))
        assertEquals(0L, Playback.plausibleDuration(-1))
        assertEquals(60_000L, Playback.plausibleDuration(60_000))
    }
}
