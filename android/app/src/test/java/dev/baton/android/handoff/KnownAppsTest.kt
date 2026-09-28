package dev.baton.android.handoff

import org.junit.Assert.assertEquals
import org.junit.Test

class KnownAppsTest {
    @Test
    fun youtubeForksAreYouTube() {
        assertEquals("youtube", KnownApps.fromPackage("app.revanced.android.youtube")?.provider)
        assertEquals("youtube", KnownApps.fromPackage("some.other.youtube.fork")?.provider)
        assertEquals("youtubemusic", KnownApps.fromPackage("app.revanced.android.apps.youtube.music")?.provider)
    }

    @Test
    fun opensTheBuildInUse() {
        val youtube = KnownApps.fromProvider("youtube")!!
        // Like the S25: ReVanced (used, updated yesterday), RVX (never opened, older), stock disabled.
        val usable = setOf("app.revanced.android.youtube", "app.rvx.android.youtube")
        val updated = mapOf("app.revanced.android.youtube" to 2_000L, "app.rvx.android.youtube" to 1_000L)
        assertEquals("app.revanced.android.youtube", KnownApps.packageFor(youtube, { it in usable }, null) { updated[it] ?: 0 })
        assertEquals("app.rvx.android.youtube", KnownApps.packageFor(youtube, { it in usable }, "app.rvx.android.youtube") { updated[it] ?: 0 })
        // A last-used build that was since disabled or removed is skipped.
        assertEquals("app.revanced.android.youtube", KnownApps.packageFor(youtube, { it in usable }, "com.google.android.youtube") { updated[it] ?: 0 })
        assertEquals("com.google.android.youtube", KnownApps.packageFor(youtube, { it == "com.google.android.youtube" }, null) { 0 })
    }
}
