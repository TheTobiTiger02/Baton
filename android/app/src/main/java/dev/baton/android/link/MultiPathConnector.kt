package dev.baton.android.link

import java.net.InetSocketAddress
import java.net.Socket
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Finds which of the paired PC's addresses answers right now - "happy eyeballs" across LAN and
 * Tailscale.
 *
 * Attempts start [STAGGER_MS] apart in the given order, so the preferred address gets a head start
 * but a dead one never makes the others wait for its timeout. An attempt only counts once the TLS
 * handshake has verified the pinned certificate: reaching *something* at an old LAN address is not
 * reaching the PC. The probe socket is closed straight away; the session connects to the winner.
 */
object MultiPathConnector {
    const val STAGGER_MS = 250L
    private const val CONNECT_TIMEOUT_MS = 4_000
    private const val RACE_TIMEOUT_MS = 8_000L

    data class Result(val candidate: EndpointCandidate, val handshakeMs: Long)

    suspend fun race(
        candidates: List<EndpointCandidate>,
        certificateFingerprint: String,
        onFailure: (EndpointCandidate) -> Unit = {}
    ): Result? {
        if (candidates.isEmpty() || certificateFingerprint.isBlank()) return null
        val factory = PinnedTls.sslContext(certificateFingerprint).first.socketFactory

        // Attempts run in their own scope: a losing probe blocked in connect() cannot be
        // interrupted, and the winner must not wait for it. Losers close their own sockets.
        val attempts = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val winner = CompletableDeferred<Result?>()
        val jobs = candidates.mapIndexed { index, candidate ->
            attempts.launch {
                delay(index * STAGGER_MS)
                if (winner.isCompleted) return@launch
                val result = probe(candidate, factory)
                if (result != null) winner.complete(result) else onFailure(candidate)
            }
        }
        attempts.launch {
            jobs.joinAll()
            winner.complete(null)
        }

        return try {
            withTimeoutOrNull(RACE_TIMEOUT_MS) { winner.await() }
        } finally {
            attempts.cancel()
        }
    }

    private fun probe(candidate: EndpointCandidate, factory: SSLSocketFactory): Result? {
        val started = System.nanoTime()
        val raw = Socket()
        return try {
            raw.connect(InetSocketAddress(candidate.host, candidate.port), CONNECT_TIMEOUT_MS)
            raw.soTimeout = CONNECT_TIMEOUT_MS
            val tls = factory.createSocket(raw, candidate.host, candidate.port, true) as SSLSocket
            tls.use { it.startHandshake() }
            Result(candidate, (System.nanoTime() - started) / 1_000_000L)
        } catch (_: Exception) {
            runCatching { raw.close() }
            null
        }
    }
}
