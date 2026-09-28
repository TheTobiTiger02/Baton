package dev.baton.android.link

import dev.baton.android.protocol.PeerInfo
import dev.baton.android.protocol.Wire
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

enum class LinkPhase {
    /** No PC paired yet. */
    Unpaired,

    /** Pairing with a PC right now. */
    Pairing,

    /** Looking for the paired PC on the network. */
    Searching,

    /** Found it; TLS and the challenge are in progress. */
    Connecting,

    /** Authenticated; handoffs work. */
    Ready,

    /** Paired but not reachable; retrying on its own. */
    Offline
}

data class LinkStatus(val phase: LinkPhase, val detail: String = "") {
    val isReady: Boolean get() = phase == LinkPhase.Ready
}

/** A handoff as the UI and notifications show it. */
data class HandoffNotice(val requestId: String, val title: String, val text: String, val failed: Boolean, val done: Boolean)

/**
 * The app-wide view of the link: its state, the other devices, and a way to send. The
 * [LinkService] is the only writer; everything else reads the flows or calls [send].
 */
object Link {
    private val statusFlow = MutableStateFlow(LinkStatus(LinkPhase.Unpaired))
    private val peersFlow = MutableStateFlow<List<PeerInfo>>(emptyList())
    private val noticesFlow = MutableSharedFlow<HandoffNotice>(extraBufferCapacity = 16)

    @Volatile
    internal var client: LanClient? = null

    @Volatile
    var deviceId: String = ""
        internal set

    @Volatile
    var hostId: String = ""
        internal set

    /** The address the PC answered on, and its certificate pin: where stream sockets go. */
    @Volatile
    var pcAddress: String = ""
        internal set

    @Volatile
    var pcFingerprint: String = ""
        internal set

    val status: StateFlow<LinkStatus> = statusFlow.asStateFlow()
    val peers: StateFlow<List<PeerInfo>> = peersFlow.asStateFlow()
    val notices: SharedFlow<HandoffNotice> = noticesFlow.asSharedFlow()

    val isReady: Boolean get() = statusFlow.value.isReady

    /** Queues a message to the PC. False when the link is not ready. */
    inline fun <reified T> send(type: String, payload: T): Boolean {
        if (!isReady) return false
        return sendRaw(Wire.encode(type, deviceId, payload))
    }

    fun sendRaw(json: String): Boolean = client?.send(json) == true

    internal fun setStatus(status: LinkStatus) {
        statusFlow.value = status
    }

    internal fun setPeers(peers: List<PeerInfo>) {
        peersFlow.value = peers
    }

    fun notice(notice: HandoffNotice) {
        noticesFlow.tryEmit(notice)
    }
}
