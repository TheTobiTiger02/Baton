package dev.baton.android.link

import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.atomic.AtomicLong

/**
 * One pinned WebSocket to the PC. Every connection bumps a generation, and callbacks from an
 * older socket are dropped, so a slow close can never tear down the connection that replaced it.
 */
class LanClient {
    private val generation = AtomicLong(0L)
    private val socketLock = Any()
    private var webSocket: WebSocket? = null

    fun connect(url: String, pinnedCertificateFingerprint: String, listener: Listener): Long {
        val connectionGeneration = generation.incrementAndGet()
        synchronized(socketLock) {
            webSocket?.close(1000, "Reconnecting")
            webSocket = null
        }
        val request = Request.Builder().url(url).build()
        val socket = PinnedTls.newClient(pinnedCertificateFingerprint).newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (!isCurrent(connectionGeneration, webSocket)) return
                listener.onOpen()
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (!isCurrent(connectionGeneration, webSocket)) return
                listener.onMessage(text)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (!clearIfCurrent(connectionGeneration, webSocket)) return
                listener.onFailed(t)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (!clearIfCurrent(connectionGeneration, webSocket)) return
                listener.onClosed(code, reason)
            }
        })
        synchronized(socketLock) {
            if (generation.get() == connectionGeneration) {
                webSocket = socket
            } else {
                socket.cancel()
            }
        }
        return connectionGeneration
    }

    fun send(json: String): Boolean = synchronized(socketLock) { webSocket?.send(json) == true }

    fun disconnect(reason: String = "User disconnected") {
        generation.incrementAndGet()
        synchronized(socketLock) {
            webSocket?.close(1000, reason)
            webSocket = null
        }
    }

    fun cancel() {
        generation.incrementAndGet()
        synchronized(socketLock) {
            webSocket?.cancel()
            webSocket = null
        }
    }

    private fun isCurrent(connectionGeneration: Long, socket: WebSocket): Boolean =
        generation.get() == connectionGeneration && synchronized(socketLock) { webSocket === socket }

    private fun clearIfCurrent(connectionGeneration: Long, socket: WebSocket): Boolean {
        if (generation.get() != connectionGeneration) return false
        synchronized(socketLock) {
            if (webSocket !== socket || generation.get() != connectionGeneration) return false
            webSocket = null
            return true
        }
    }

    /** [onFailed] and [onClosed] are the two ways a socket ends; exactly one of them fires. */
    interface Listener {
        fun onOpen() = Unit
        fun onMessage(message: String)
        fun onFailed(error: Throwable)
        fun onClosed(code: Int, reason: String)
    }
}
