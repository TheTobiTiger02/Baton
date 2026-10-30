package dev.baton.android.link

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class WakeOnLanTest {
    @Test
    fun magicPacketIsSyncThenSixteenAddresses() {
        val packet = WakeOnLan.magicPacket("AA:bb:01:02:03:FF")!!
        assertEquals(102, packet.size)
        assertArrayEquals(ByteArray(6) { 0xFF.toByte() }, packet.copyOfRange(0, 6))
        val address = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 1, 2, 3, 0xFF.toByte())
        for (repeat in 0 until 16) assertArrayEquals(address, packet.copyOfRange(6 + repeat * 6, 12 + repeat * 6))
    }

    @Test
    fun rejectsWhatIsNotAnAddress() {
        assertNull(WakeOnLan.magicPacket("AA:BB:CC"))
        assertNull(WakeOnLan.magicPacket("ZZ:BB:CC:DD:EE:FF"))
    }
}
