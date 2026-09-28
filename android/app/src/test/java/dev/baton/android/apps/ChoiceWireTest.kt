package dev.baton.android.apps

import dev.baton.android.protocol.ChoiceKeys
import dev.baton.android.protocol.ChoiceKinds
import dev.baton.android.protocol.HandoffChoice
import dev.baton.android.protocol.Platforms
import dev.baton.android.protocol.Wire
import org.junit.Assert.assertEquals
import org.junit.Test

/** Same JSON and keys as Baton.Tests.AppMatcherTests: the PC stores what the phone sends. */
class ChoiceWireTest {
    @Test
    fun decodesThePcsChoice() {
        val json = """{"kind":"app","label":"WhatsApp","appId":"5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App","remember":true}"""
        val choice = Wire.json.decodeFromString(HandoffChoice.serializer(), json)
        assertEquals(HandoffChoice(ChoiceKinds.APP, "WhatsApp", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", remember = true), choice)
    }

    @Test
    fun keysMatchThePc() {
        assertEquals("android:com.whatsapp->windows", ChoiceKeys.of(Platforms.ANDROID, "com.WhatsApp", Platforms.WINDOWS))
    }
}

class ChoiceSubjectTest {
    private fun inZen(url: String?, provider: String) = dev.baton.android.protocol.Activity(
        "media:zen", "pc", dev.baton.android.protocol.ActivityKind.WebMedia, "t",
        dev.baton.android.protocol.ActivityApp("Zen", "F0DC299D809B9700"), "2026-01-01T00:00:00Z",
        url = url, content = dev.baton.android.protocol.ActivityContent(provider))

    @Test
    fun sameKeysAsThePc() {
        assertEquals("windows:site:youtube->android", ChoiceKeys.of(Platforms.WINDOWS, inZen(null, "youtube"), Platforms.ANDROID))
        assertEquals("windows:site:x.com->android", ChoiceKeys.of(Platforms.WINDOWS, inZen("https://x.com/home", "web"), Platforms.ANDROID))
        assertEquals("windows:site:web->android", ChoiceKeys.of(Platforms.WINDOWS, inZen(null, "web"), Platforms.ANDROID))
    }
}
