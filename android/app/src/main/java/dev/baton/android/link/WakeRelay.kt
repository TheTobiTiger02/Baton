package dev.baton.android.link

import android.content.Context
import android.util.Base64
import android.util.Log
import dev.baton.android.protocol.WakeRelay as RelayInfo
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.math.abs
import org.json.JSONArray
import org.json.JSONObject

/**
 * Waking the PC from away. Magic packets don't cross the internet, so a device at home that
 * offers to (a tablet, an old phone) listens on [PORT] and sends them on its own network when a
 * signed request arrives over Tailscale. Requests are signed with the key the PC gives its
 * paired devices, carry the time and a random nonce, and are answered with a signed
 * acknowledgement, so a stranger can neither wake the PC nor fake that it was woken.
 */
object WakeRelay {
    private const val TAG = "BatonWakeRelay"
    const val PORT = 7840
    private const val KEY_KEY = "wake_key"
    private const val KEY_RELAYS = "wake_relays"
    private val REQUEST = "BWK1".toByteArray()
    private val ACK = "BWA1".toByteArray()
    private const val NONCE = 16
    private const val SIGNATURE = 32
    private const val MAX_SKEW_MS = 120_000L
    private const val ACK_WAIT_MS = 3_000

    @Volatile private var socket: DatagramSocket? = null
    private val seen = LinkedHashSet<String>()

    // ---- What the PC told this device ----

    fun remember(context: Context, key: String?, relays: List<RelayInfo>?) {
        val edit = prefs(context).edit()
        if (key != null) edit.putString(KEY_KEY, key)
        if (relays != null) {
            val array = JSONArray()
            relays.forEach { relay -> array.put(JSONObject().put("deviceId", relay.deviceId).put("name", relay.name).put("addresses", JSONArray(relay.addresses))) }
            edit.putString(KEY_RELAYS, array.toString())
        }
        edit.apply()
    }

    private fun key(context: Context): ByteArray? =
        prefs(context).getString(KEY_KEY, null)?.let { runCatching { Base64.decode(it, Base64.NO_WRAP) }.getOrNull() }

    /** The other devices that offered, never this one. */
    fun relays(context: Context, selfId: String?): List<RelayInfo> = runCatching {
        val array = JSONArray(prefs(context).getString(KEY_RELAYS, null) ?: return emptyList())
        (0 until array.length()).map { index ->
            array.getJSONObject(index).let { item ->
                val addresses = item.getJSONArray("addresses")
                RelayInfo(item.getString("deviceId"), item.getString("name"), (0 until addresses.length()).map(addresses::getString))
            }
        }.filter { it.deviceId != selfId }
    }.getOrDefault(emptyList())

    // ---- Asking (the phone away from home) ----

    /**
     * Asks every relay to wake the PC; blocking, so call it off the main thread. Returns the name
     * of the first relay that confirmed, or null when none answered.
     */
    fun ask(context: Context, selfId: String?): String? {
        val key = key(context) ?: return null
        val relays = relays(context, selfId)
        if (relays.isEmpty()) return null
        val nonce = ByteArray(NONCE).also(SecureRandom()::nextBytes)
        val packet = request(key, System.currentTimeMillis(), nonce)
        DatagramSocket().use { socket ->
            for (relay in relays) for (address in relay.addresses) {
                runCatching { socket.send(DatagramPacket(packet, packet.size, InetAddress.getByName(address), PORT)) }
                    .onFailure { Log.d(TAG, "Request to $address failed: ${it.message}") }
            }
            socket.soTimeout = ACK_WAIT_MS
            val buffer = ByteArray(64)
            val deadline = System.currentTimeMillis() + ACK_WAIT_MS
            while (System.currentTimeMillis() < deadline) {
                val answer = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(answer)
                } catch (_: SocketTimeoutException) {
                    break
                }
                if (isAck(key, buffer.copyOf(answer.length), nonce)) {
                    val from = answer.address.hostAddress
                    val relay = relays.firstOrNull { from in it.addresses }?.name ?: "A device at home"
                    LinkDiagnostics.record("wake.relayed", relay)
                    return relay
                }
            }
        }
        LinkDiagnostics.record("wake.relay.silent", "${relays.size} relay(s)")
        return null
    }

    // ---- Relaying (the device at home) ----

    /** Listens while this device offers to relay; harmless to call again. */
    fun start(context: Context) {
        if (socket != null) return
        val app = context.applicationContext
        val listening = runCatching { DatagramSocket(PORT) }.getOrElse {
            Log.w(TAG, "Can't listen on $PORT", it)
            return
        }
        socket = listening
        Thread({
            val buffer = ByteArray(128)
            while (!listening.isClosed) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    listening.receive(packet)
                } catch (_: java.io.IOException) {
                    break
                }
                val key = key(app) ?: continue
                val nonce = verifyRequest(key, buffer.copyOf(packet.length), System.currentTimeMillis()) ?: continue
                // A request seen before is a replay: once is enough.
                val fresh = synchronized(seen) {
                    seen.add(nonce.toHex()).also { while (seen.size > 256) seen.remove(seen.first()) }
                }
                if (!fresh) continue
                val sent = WakeOnLan.wake(app)
                LinkDiagnostics.record("wake.relay", "for ${packet.address.hostAddress}: ${if (sent) "sent" else "nothing to send"}")
                if (sent) {
                    val ack = ack(key, nonce)
                    runCatching { listening.send(DatagramPacket(ack, ack.size, packet.address, packet.port)) }
                }
            }
        }, "BatonWakeRelay").apply { isDaemon = true }.start()
    }

    fun stop() {
        socket?.close()
        socket = null
    }

    /** Where this device can be asked: Tailscale first (what works from away), then its home network. */
    fun addresses(): List<String> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .filter { it.isSiteLocalAddress || isTailscale(it) }
            .sortedBy { if (isTailscale(it)) 0 else 1 }
            .mapNotNull { it.hostAddress }
            .distinct()
    }.getOrDefault(emptyList())

    fun hasTailscale(): Boolean = addresses().any { address -> InetAddress.getByName(address).let { it is Inet4Address && isTailscale(it) } }

    private fun isTailscale(address: Inet4Address): Boolean =
        address.address.let { it[0].toInt() and 0xFF == 100 && (it[1].toInt() and 0xC0) == 64 }

    // ---- The packets ----

    /** "BWK1", the time (ms, big-endian), the nonce, then the signature of all that. */
    fun request(key: ByteArray, nowMs: Long, nonce: ByteArray): ByteArray {
        val body = REQUEST + java.nio.ByteBuffer.allocate(8).putLong(nowMs).array() + nonce
        return body + sign(key, body)
    }

    /** The nonce of a genuine, recent request; null for anything else. */
    fun verifyRequest(key: ByteArray, packet: ByteArray, nowMs: Long): ByteArray? {
        if (packet.size != REQUEST.size + 8 + NONCE + SIGNATURE || !packet.copyOfRange(0, REQUEST.size).contentEquals(REQUEST)) return null
        val body = packet.copyOfRange(0, packet.size - SIGNATURE)
        if (!MessageDigest.isEqual(sign(key, body), packet.copyOfRange(body.size, packet.size))) return null
        val sentAt = java.nio.ByteBuffer.wrap(packet, REQUEST.size, 8).long
        if (abs(nowMs - sentAt) > MAX_SKEW_MS) return null
        return packet.copyOfRange(REQUEST.size + 8, REQUEST.size + 8 + NONCE)
    }

    fun ack(key: ByteArray, nonce: ByteArray): ByteArray = (ACK + nonce).let { it + sign(key, it) }

    fun isAck(key: ByteArray, packet: ByteArray, nonce: ByteArray): Boolean =
        packet.size == ACK.size + NONCE + SIGNATURE && MessageDigest.isEqual(packet, ack(key, nonce))

    private fun sign(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)

    private fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }

    private fun prefs(context: Context) = context.getSharedPreferences("baton_link", Context.MODE_PRIVATE)
}
