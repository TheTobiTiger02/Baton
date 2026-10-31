package dev.baton.android.link

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WakeRelayTest {
    private val key = ByteArray(32) { it.toByte() }
    private val nonce = ByteArray(16) { (it * 7).toByte() }
    private val now = 1_800_000_000_000L

    @Test
    fun aSignedRecentRequestIsAccepted() {
        val packet = WakeRelay.request(key, now, nonce)
        assertArrayEquals(nonce, WakeRelay.verifyRequest(key, packet, now + 5_000))
    }

    @Test
    fun aRequestWithTheWrongKeyOrChangedIsRefused() {
        val packet = WakeRelay.request(key, now, nonce)
        assertNull(WakeRelay.verifyRequest(ByteArray(32), packet, now))
        val changed = packet.copyOf().also { it[10] = (it[10] + 1).toByte() }
        assertNull(WakeRelay.verifyRequest(key, changed, now))
        assertNull(WakeRelay.verifyRequest(key, packet.copyOf(packet.size - 1), now))
    }

    @Test
    fun anOldRequestIsRefused() {
        val packet = WakeRelay.request(key, now, nonce)
        assertNull(WakeRelay.verifyRequest(key, packet, now + 10 * 60_000))
        assertNull(WakeRelay.verifyRequest(key, packet, now - 10 * 60_000))
    }

    @Test
    fun onlyTheRightAcknowledgementCounts() {
        val ack = WakeRelay.ack(key, nonce)
        assertTrue(WakeRelay.isAck(key, ack, nonce))
        assertFalse(WakeRelay.isAck(key, ack, ByteArray(16)))
        assertFalse(WakeRelay.isAck(ByteArray(32), ack, nonce))
    }
}
