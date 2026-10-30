package dev.baton.android.link

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LinkEventTest {
    @Test
    fun encodeAndDecodeRoundTrip() {
        val event = LinkEvent(1_000L, "drop.socket", "SocketTimeoutException: timeout, after 4 min", count = 3, lastAt = 9_000L)
        assertEquals(event, LinkEvent.decode(event.encode()))
    }

    @Test
    fun tabsAndNewlinesInDetailStayOnOneLine() {
        val decoded = LinkEvent.decode(LinkEvent(1L, "pc.error", "a\tb\nc").encode())
        assertEquals("a b c", decoded?.detail)
    }

    @Test
    fun linesFromBeforeCountsExistStillDecode() {
        assertEquals(LinkEvent(5L, "find.ok", "lan 10.0.0.2:7838"), LinkEvent.decode("5\tfind.ok\tlan 10.0.0.2:7838"))
    }

    @Test
    fun brokenLinesAreSkipped() {
        assertNull(LinkEvent.decode(""))
        assertNull(LinkEvent.decode("not-a-time\tfind.ok"))
        assertNull(LinkEvent.decode("5\t"))
    }

    @Test
    fun repeatedEventsFoldIntoOne() {
        var events = emptyList<LinkEvent>()
        events = LinkEvent.append(events, LinkEvent(1L, "find.none", "lan and discovery: no answer"), 10)
        events = LinkEvent.append(events, LinkEvent(2L, "find.none", "lan and discovery: no answer"), 10)
        events = LinkEvent.append(events, LinkEvent(3L, "find.none", "lan and discovery: no answer"), 10)
        assertEquals(listOf(LinkEvent(1L, "find.none", "lan and discovery: no answer", count = 3, lastAt = 3L)), events)
    }

    @Test
    fun differentDetailStartsANewEntry() {
        var events = emptyList<LinkEvent>()
        events = LinkEvent.append(events, LinkEvent(1L, "find.none", "lan"), 10)
        events = LinkEvent.append(events, LinkEvent(2L, "find.none", "tailscale"), 10)
        assertEquals(2, events.size)
    }

    @Test
    fun oldestEntriesFallOffAtCapacity() {
        var events = emptyList<LinkEvent>()
        (1L..5L).forEach { events = LinkEvent.append(events, LinkEvent(it, "event$it"), 3) }
        assertEquals(listOf("event3", "event4", "event5"), events.map { it.event })
    }

    @Test
    fun causesCountDropsAndExitReasonsSinceTheCutoff() {
        val events = listOf(
            LinkEvent(1L, "drop.stale", "after 3 min"),
            LinkEvent(100L, "drop.stale", "after 1 min"),
            LinkEvent(110L, "drop.socket", "SocketException: reset", count = 2, lastAt = 120L),
            LinkEvent(130L, "process.exit", "low-memory killed while cached"),
            LinkEvent(135L, "process.exit", "package-updated stop dev.baton.android due to installPackageLI"),
            LinkEvent(140L, "session.ready", "lan 10.0.0.2:7838"),
            LinkEvent(150L, "drop.stale", "after 2 min")
        )
        assertEquals(
            listOf("drop.socket" to 2, "drop.stale" to 2, "process.exit low-memory" to 1),
            LinkEvent.causes(events, since = 50L)
        )
    }
}
