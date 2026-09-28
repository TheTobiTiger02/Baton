package dev.baton.android.link

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class SessionAuthenticationTest {
    @Test
    fun challengeProofMatchesHostContract() {
        val secret = (0..31).joinToString("") { "%02x".format(it) }
        val proof = SessionAuthentication.challengeProof(
            trustSecret = secret,
            challengeId = "challenge-1",
            nonce = "nonce-1",
            deviceId = "android-device",
            hostId = "host-1"
        )
        assertEquals("910c5114e936b0b3cb8c5db8f848bca9f68b58aa72206ea4e508f753c3e67a13", proof)
        assertNotEquals(proof, SessionAuthentication.challengeProof(secret, "challenge-1", "nonce-2", "android-device", "host-1"))
    }
}
