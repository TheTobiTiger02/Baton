package dev.baton.android.link

import org.junit.Assert.assertEquals
import org.junit.Test

class NearbyBeaconTest {
    @Test
    fun theCodeMatchesThePcs() {
        // The same vector as NearbyDevicesTests.cs: "BN", version 1, then the token.
        val payload = NearbyBeacon.payload(ByteArray(32) { it.toByte() }, 1234)
        assertEquals("424e017409163e5d578140", payload.joinToString("") { "%02x".format(it) })
    }
}
