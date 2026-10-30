package dev.baton.android.link

import android.content.Context
import android.util.Log
import dev.baton.android.protocol.WakeTarget
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import org.json.JSONArray
import org.json.JSONObject

/**
 * Wakes the paired PC from sleep or off: the magic packet (six 0xFF bytes, then the network card's
 * address sixteen times) to its network's broadcast address, from a phone on the same network.
 * The PC announces its cards while connected; the PC itself must allow waking (its network card's
 * power settings, and for "off" also the firmware).
 */
object WakeOnLan {
    private const val TAG = "BatonWake"
    private const val KEY_TARGETS = "wake_targets"
    private val PORTS = intArrayOf(9, 7)

    fun remember(context: Context, targets: List<WakeTarget>) {
        if (targets.isEmpty()) return
        val array = JSONArray()
        targets.forEach { array.put(JSONObject().put("mac", it.mac).put("broadcast", it.broadcast)) }
        prefs(context).edit().putString(KEY_TARGETS, array.toString()).apply()
    }

    fun targets(context: Context): List<WakeTarget> = runCatching {
        val array = JSONArray(prefs(context).getString(KEY_TARGETS, null) ?: return emptyList())
        (0 until array.length()).map { array.getJSONObject(it).let { item -> WakeTarget(item.getString("mac"), item.getString("broadcast")) } }
    }.getOrDefault(emptyList())

    fun canWake(context: Context): Boolean = targets(context).isNotEmpty()

    /** Sends the packets; blocking, so call it off the main thread. True when at least one went out. */
    fun wake(context: Context): Boolean {
        var sent = false
        DatagramSocket().use { socket ->
            socket.broadcast = true
            for (target in targets(context)) {
                val packet = magicPacket(target.mac) ?: continue
                for (address in listOf(target.broadcast, "255.255.255.255").distinct()) {
                    for (port in PORTS) {
                        runCatching { socket.send(DatagramPacket(packet, packet.size, InetAddress.getByName(address), port)) }
                            .onSuccess { sent = true }
                            .onFailure { Log.w(TAG, "Wake packet to $address:$port failed", it) }
                    }
                }
            }
        }
        LinkDiagnostics.record("wake.sent", if (sent) "magic packet" else "nothing sent")
        return sent
    }

    /** Six 0xFF, then the six-byte address sixteen times; null for an address that isn't one. */
    fun magicPacket(mac: String): ByteArray? {
        val address = mac.split(':', '-').takeIf { it.size == 6 }?.map { it.toIntOrNull(16)?.toByte() ?: return null } ?: return null
        return ByteArray(6) { 0xFF.toByte() } + (1..16).flatMap { address }.toByteArray()
    }

    private fun prefs(context: Context) = context.getSharedPreferences("baton_link", Context.MODE_PRIVATE)
}
