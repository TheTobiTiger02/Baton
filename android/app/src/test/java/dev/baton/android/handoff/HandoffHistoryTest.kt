package dev.baton.android.handoff

import dev.baton.android.protocol.HandoffModes
import dev.baton.android.protocol.HandoffStatus
import org.junit.Assert.*
import org.junit.Test

class HandoffHistoryTest {
    private val intent = HandoffIntent("source", "destination", "original", HandoffModes.AUTO, null, null)

    @Test fun allResultsRetainTheirIntentAndIgnoreDuplicates() {
        HandoffStatus.entries.forEach { status ->
            val history = HandoffHistory()
            history.begin("id", "Video", intent, "Sending", 100)
            assertNotNull(history.finish("id", status, "Result"))
            assertNull(history.finish("id", status, "Duplicate"))
            history.begin("id", "Other activity", intent.copy(activityId = "other"), "Opening", 200)
            assertEquals(status, history.recent.value.single().status)
            assertEquals("original", history.find("id")!!.intent.activityId)
            assertEquals(100L, history.find("id")!!.startedAt)
        }
    }

    @Test fun timeoutIsNotFailureAndLateResultResolvesSameRecord() {
        val history = HandoffHistory()
        history.begin("id", "Video", intent, "Sending", 0)
        assertTrue(history.expire(119_999).isEmpty())
        assertTrue(history.expire(120_000).single().unconfirmed)
        assertNull(history.find("id")!!.status)
        assertTrue(history.expire(240_000).isEmpty())
        history.begin("id", "Video", intent, "Late progress", 300_000)
        assertTrue(history.find("id")!!.unconfirmed)
        assertNotNull(history.finish("id", HandoffStatus.Opened, "Opened"))
        assertEquals(1, history.recent.value.size)
        assertFalse(history.find("id")!!.unconfirmed)
    }

    @Test fun latestFortyAreNewestFirstAndClearSuppressesLateActivity() {
        val history = HandoffHistory()
        repeat(45) { history.begin("r$it", "Video", intent, "Sending", it.toLong()) }
        assertEquals(40, history.recent.value.size)
        assertEquals("r44", history.recent.value.first().requestId)
        assertNull(history.find("r0"))
        history.begin("r0", "Late", intent, "Opening")
        assertNull(history.find("r0"))
        history.clear()
        history.begin("r44", "Late delivery", intent, "Opening")
        assertNull(history.finish("r44", HandoffStatus.Opened, "Opened"))
        assertTrue(history.recent.value.isEmpty())
        assertTrue(HandoffHistory().recent.value.isEmpty())
    }

    @Test fun relayKeepsOriginalDevicesAndFailureEnablesRecovery() {
        val history = HandoffHistory()
        history.begin("relay", "Video", intent, "Requesting", 0)
        history.begin("relay", "Video", intent.copy(mode = HandoffModes.STREAM), "Received", 1)
        assertEquals(intent, history.find("relay")!!.intent)
        assertTrue(history.finish("relay", HandoffStatus.Failed, "Offline")!!.recoverable)
    }

    @Test fun viewerOpeningCannotHideLaterConsentFailure() {
        val history = HandoffHistory()
        history.begin("stream", "Page", intent.copy(mode = HandoffModes.STREAM), "Consent needed")
        history.finish("stream", HandoffStatus.Opened, "Viewer opened")
        assertNotNull(history.finish("stream", HandoffStatus.Failed, "Consent declined"))
        assertEquals(HandoffStatus.Failed, history.find("stream")!!.status)
        assertNull(history.finish("stream", HandoffStatus.Failed, "Consent declined"))
    }

    @Test fun automaticWindowStreamingKeepsErrorsWhileAnExplicitAppChoiceStaysNative() {
        val app = dev.baton.android.protocol.Activity("window", "pc", dev.baton.android.protocol.ActivityKind.WindowStream,
            "App", dev.baton.android.protocol.ActivityApp("App", "app"), "2026-09-29T20:00:00Z")
        val automatic = intent.copy(activity = app)
        assertTrue(automatic.streaming)
        assertFalse(automatic.copy(choice = dev.baton.android.protocol.HandoffChoice(dev.baton.android.protocol.ChoiceKinds.APP, "App", "app")).streaming)
    }
}
