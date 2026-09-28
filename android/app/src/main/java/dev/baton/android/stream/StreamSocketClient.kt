package dev.baton.android.stream

import android.util.Base64
import dev.baton.android.link.PinnedTls
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.ssl.SSLSocket

/**
 * Dials the PC's media stream port and presents the admission ticket.
 *
 * Separate from the control WebSocket on purpose: video must not queue behind clipboard or
 * notification traffic, and the same socket code has to serve the ADB reverse tunnel, which has no
 * WebSocket and no certificate to pin against.
 */
class StreamSocketClient private constructor(
    private val socket: Socket,
    val output: OutputStream,
    val input: InputStream
) : Closeable {

    override fun close() {
        runCatching { output.flush() }
        runCatching { socket.close() }
    }

    companion object {

        private const val CONNECT_TIMEOUT_MS = 10_000

        /**
         * Connects, completes the handshake, and returns a socket positioned for records.
         *
         * [certificateFingerprint] is required whenever [useTls] is true - the host certificate is
         * self-signed, so the pin is the only identity check there is. The ADB tunnel passes
         * useTls = false because it is loopback-to-loopback inside the debugging trust boundary.
         */
        fun connect(
            host: String,
            port: Int,
            kind: Int,
            ticketBase64: String,
            streamId: Long,
            useTls: Boolean,
            certificateFingerprint: String?
        ): StreamSocketClient {
            val ticket = Base64.decode(ticketBase64, Base64.DEFAULT)
            require(ticket.size == StreamProtocol.TICKET_BYTES) {
                "Stream ticket must be ${StreamProtocol.TICKET_BYTES} bytes, was ${ticket.size}."
            }

            val socket = if (useTls) {
                require(!certificateFingerprint.isNullOrBlank()) {
                    "A certificate fingerprint is required for a TLS stream socket."
                }

                val (context, _) = PinnedTls.sslContext(certificateFingerprint)
                (context.socketFactory.createSocket() as SSLSocket).apply {
                    // Frames are latency-sensitive and already sized by the encoder; Nagle would
                    // add up to a frame of delay for nothing.
                    tcpNoDelay = true
                    connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
                    startHandshake()
                }
            } else {
                Socket().apply {
                    tcpNoDelay = true
                    connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
                }
            }

            return try {
                val output = BufferedOutputStream(socket.getOutputStream(), OUTPUT_BUFFER_BYTES)
                output.write(StreamProtocol.encodeHandshake(kind, ticket, streamId))
                output.flush()
                StreamSocketClient(socket, output, socket.getInputStream())
            } catch (e: Exception) {
                runCatching { socket.close() }
                throw e
            }
        }

        /**
         * One buffer big enough for a keyframe. Smaller and a large frame turns into several
         * writes; larger and the buffer itself starts holding frames back.
         */
        private const val OUTPUT_BUFFER_BYTES = 256 * 1024
    }
}
