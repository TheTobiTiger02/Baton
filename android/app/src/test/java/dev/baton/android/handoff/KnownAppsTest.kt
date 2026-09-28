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
        // Like the S21: ReVanced and stock both installed; the store updates stock more often.
        val usable = setOf("app.revanced.android.youtube", "com.google.android.youtube")
        assertEquals("app.revanced.android.youtube", KnownApps.packageFor(youtube, { it in usable }, null))
        assertEquals("com.google.android.youtube", KnownApps.packageFor(youtube, { it in usable }, "com.google.android.youtube"))
        // A last-used build that was since disabled or removed is skipped.
        assertEquals("app.revanced.android.youtube", KnownApps.packageFor(youtube, { it in usable }, "app.rvx.android.youtube"))
        assertEquals("com.google.android.youtube", KnownApps.packageFor(youtube, { it == "com.google.android.youtube" }, null))
    }
}
