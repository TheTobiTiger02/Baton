package dev.baton.android.link

import android.util.Base64
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import okhttp3.OkHttpClient

object PinnedTls {

    /**
     * Trusts exactly one certificate, identified by its SHA-256 fingerprint.
     *
     * The host certificate is self-signed and has no public DNS name, so the pin is the identity
     * check rather than an addition to one. Exposed separately from [newClient] because the media
     * stream socket is a plain `SSLSocket` rather than an OkHttp connection, and duplicating the
     * pin comparison for it would be a second place to get this wrong.
     */
    fun trustManager(expectedFingerprint: String): X509TrustManager {
        val expected = expectedFingerprint.trim().removePrefix("sha256/")
        require(expected.isNotBlank()) { "A certificate fingerprint is required for pinned TLS." }

        return object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
                throw java.security.cert.CertificateException("Client certificates are not accepted.")

            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val certificate = chain?.firstOrNull()
                    ?: throw java.security.cert.CertificateException("The PC did not present a certificate.")
                val digest = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
                val hex = digest.joinToString("") { "%02x".format(it) }
                val base64 = Base64.encodeToString(digest, Base64.NO_WRAP)
                val expectedHex = expected.replace(":", "").replace("-", "")
                val matchesHex = expectedHex.length == 64 &&
                    expectedHex.all { it.isDigit() || it.lowercaseChar() in 'a'..'f' } &&
                    MessageDigest.isEqual(expectedHex.lowercase().toByteArray(), hex.toByteArray())
                val matchesBase64 = MessageDigest.isEqual(expected.toByteArray(), base64.toByteArray())
                if (!matchesHex && !matchesBase64) {
                    throw java.security.cert.CertificateException(
                        "The PC certificate no longer matches the saved pairing."
                    )
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
    }

    fun sslContext(expectedFingerprint: String): Pair<SSLContext, X509TrustManager> {
        val manager = trustManager(expectedFingerprint)
        val context = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(manager), SecureRandom())
        }
        return context to manager
    }

    fun newClient(expectedFingerprint: String): OkHttpClient {
        val (context, manager) = sslContext(expectedFingerprint)
        return OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS)
            .writeTimeout(15, TimeUnit.SECONDS)
            .pingInterval(20, TimeUnit.SECONDS)
            .sslSocketFactory(context.socketFactory, manager)
            // The stable certificate pin is the identity check; the self-signed certificate has no
            // public DNS name to verify against.
            .hostnameVerifier { _, _ -> true }
            .build()
    }
}
