package dev.baton.android.link

import android.util.Log
import dev.baton.android.protocol.DiscoveryResponse
import dev.baton.android.protocol.Wire
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** A PC that answered the UDP broadcast, at the address it answered from. */
data class DiscoveredPc(val address: String, val response: DiscoveryResponse) {
    val hostId: String get() = response.hostId
    val name: String get() = response.pcName
    val port: Int get() = response.wssPort
    val fingerprint: String get() = response.certificateFingerprint
}

/** Finds Baton PCs on the local network with one UDP broadcast to every interface. */
object LanDiscovery {
    private const val TAG = "BatonDiscovery"
    const val PORT = 7837
    private const val REQUEST = "BATON_DISCOVER_V1"

    suspend fun discover(timeoutMs: Int = 1_500): List<DiscoveredPc> = withContext(Dispatchers.IO) {
        val found = linkedMapOf<String, DiscoveredPc>()
        runCatching {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = timeoutMs
                val request = REQUEST.toByteArray(Charsets.UTF_8)
                broadcastAddresses().forEach { address ->
                    runCatching { socket.send(DatagramPacket(request, request.size, address, PORT)) }
                }

                val buffer = ByteArray(8192)
                val deadline = System.currentTimeMillis() + timeoutMs
                while (System.currentTimeMillis() < deadline) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(packet)
                    } catch (_: SocketTimeoutException) {
                        break
                    }
                    val sender = packet.address.hostAddress ?: continue
                    val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    runCatching { Wire.json.decodeFromString(DiscoveryResponse.serializer(), text) }
                        .onSuccess { found[it.hostId] = DiscoveredPc(sender, it) }
                        .onFailure { Log.d(TAG, "Ignoring a malformed discovery reply from $sender.") }
                }
            }
        }.onFailure { Log.w(TAG, "Discovery failed: ${it.message}") }
        found.values.toList()
    }

    private fun broadcastAddresses(): Set<InetAddress> {
        val addresses = linkedSetOf(InetAddress.getByName("255.255.255.255"))
        runCatching {
            NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.interfaceAddresses.orEmpty() }
                .mapNotNullTo(addresses) { it.broadcast }
        }
        return addresses
    }
}
