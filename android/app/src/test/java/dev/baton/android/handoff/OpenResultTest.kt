package dev.baton.android.handoff

import dev.baton.android.protocol.HandoffStatus
import org.junit.Assert.*
import org.junit.Test

class OpenResultTest {
    @Test fun notificationFallbackDoesNotClaimTheAppOpened() {
        val result = Openers.launchResult(false)
        assertEquals(HandoffStatus.Fallback, result.status)
        assertTrue(result.detail!!.contains("hasn't opened"))
    }

    @Test fun appLaunchDoesNotClaimPlaybackOrPositionConfirmed() {
        val result = Openers.launchResult(true)
        assertEquals(HandoffStatus.Opened, result.status)
        assertTrue(result.detail!!.contains("not confirmed"))
    }
}
