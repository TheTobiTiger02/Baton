package dev.baton.android.apps

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AppUpdatesTest {
    @Test
    fun comparesVersionsByNumberNotText() {
        assertTrue(AppUpdates.isNewer("1.10.0", "1.9.3"))
        assertTrue(AppUpdates.isNewer("0.2.0", "0.1.0"))
        assertTrue(AppUpdates.isNewer("1.0.1", "1.0"))
        assertFalse(AppUpdates.isNewer("0.1.0", "0.1.0"))
        assertFalse(AppUpdates.isNewer("0.0.9", "0.1.0"))
        assertFalse(AppUpdates.isNewer("nightly", "0.1.0"))
    }
}
