package dev.baton.android.stream

import org.junit.Assert.assertEquals
import org.junit.Test

/** Same vectors as Baton.Tests.MediaTests: the Kotlin and C# codecs must agree byte for byte. */
class ControlMessageCodecTest {
    private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02X".format(it) }

    private fun bytes(hex: String) = ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    private val vectors = listOf(
        "0100000000000000000700000064000000C807800438FFFF" to ControlMessageCodec.touch(ControlMessageCodec.TOUCH_DOWN, 7, 100, 200, 1920, 1080),
        "020000000A0000001407800438FFFF0002" to ControlMessageCodec.scroll(10, 20, 1920, 1080, -1, 2),
        "03000000001D0000000000001000" to ControlMessageCodec.key(ControlMessageCodec.KEY_DOWN, 29, 0, 0x1000),
        "040000000368C3A9" to ControlMessageCodec.text("hé"),
        "0500" to ControlMessageCodec.navigate(ControlMessageCodec.NAV_BACK),
        "08" to ControlMessageCodec.keyframeRequest(),
        "0901" to ControlMessageCodec.rotate(1),
        "0BFFFFFFFB0000000C" to ControlMessageCodec.mouseMove(-5, 12),
        "0C0201" to ControlMessageCodec.mouseButton(ControlMessageCodec.BUTTON_RIGHT, ControlMessageCodec.KEY_UP),
    )

    @Test
    fun encodesLikeThePc() {
        vectors.forEach { (expected, encoded) -> assertEquals(expected, hex(encoded)) }
    }

    @Test
    fun decodesWhatItEncodes() {
        assertEquals(ControlMessage.Touch(ControlMessageCodec.TOUCH_DOWN, 7, 100, 200, 1920, 1080, 65535),
            ControlMessageCodec.decode(bytes(vectors[0].first)))
        assertEquals(ControlMessage.Scroll(10, 20, 1920, 1080, -1, 2), ControlMessageCodec.decode(bytes(vectors[1].first)))
        assertEquals(ControlMessage.Text("hé"), ControlMessageCodec.decode(bytes(vectors[3].first)))
        assertEquals(ControlMessage.MouseMove(-5, 12), ControlMessageCodec.decode(bytes(vectors[7].first)))
    }

    @Test
    fun recordHeaderCarriesTheChannelInItsSecondByte() {
        val header = StreamProtocol.encodeHeader(StreamProtocol.FLAG_KEYFRAME, 5, 1_000, 3, StreamProtocol.CHANNEL_AUDIO)
        assertEquals("0201000500000000000003E800000003", hex(header))
        val decoded = StreamProtocol.decodeHeader(header)
        assertEquals(StreamProtocol.CHANNEL_AUDIO, decoded.channel)
        assertEquals(1_000L, decoded.presentationTimeUs)
    }
}
