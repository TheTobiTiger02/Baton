package dev.baton.android.stream

import org.junit.Assert.assertEquals
import org.junit.Test

class OpusAudioTest {
    @Test
    fun headerDescribesStereoAt48kHz() {
        // "OpusHead", version 1, 2 channels, no pre-skip, 48000 Hz, no gain, mapping family 0.
        val hex = OpusAudio.opusHead().joinToString("") { "%02X".format(it) }
        assertEquals("4F707573486561640102000080BB0000000000", hex)
    }

    @Test
    fun opusFlagIsItsOwnBit() {
        val header = StreamProtocol.decodeHeader(
            StreamProtocol.encodeHeader(StreamProtocol.FLAG_OPUS, 1, 0, 4, StreamProtocol.CHANNEL_AUDIO))
        assertEquals(true, header.isOpus)
        assertEquals(false, header.isKeyframe)
    }
}
