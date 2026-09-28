package dev.baton.android.link

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object SessionAuthentication {
    fun challengeProof(
        trustSecret: String,
        challengeId: String,
        nonce: String,
        deviceId: String,
        hostId: String
    ): String {
        val keyBytes = trustSecret.takeIf { it.length % 2 == 0 && it.matches(Regex("[0-9a-fA-F]+")) }
            ?.chunked(2)
            ?.map { it.toInt(16).toByte() }
            ?.toByteArray()
            ?: trustSecret.toByteArray(Charsets.UTF_8)
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(keyBytes, "HmacSHA256"))
        val message = "$challengeId:$nonce:$deviceId:$hostId".toByteArray(Charsets.UTF_8)
        return mac.doFinal(message).joinToString("") { "%02x".format(it) }
    }
}
