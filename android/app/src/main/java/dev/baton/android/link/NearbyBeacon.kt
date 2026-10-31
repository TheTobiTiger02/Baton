package dev.baton.android.link

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Lets the PC tell when this phone is near it. The phone sends a quiet Bluetooth signal (four
 * times a second: a PC scans in short windows and hears too little of a slower one) carrying a
 * code only the paired PC can recognise: derived from the
 * pairing's key and the quarter hour, so it changes every 15 minutes and nobody else can follow
 * the phone by it. The PC measures how strong the signal is; it only ever suggests, never acts.
 */
object NearbyBeacon {
    private const val TAG = "BatonNearby"
    /** "For testing" in Bluetooth's company list: what an app without a registered id uses. */
    const val COMPANY_ID = 0xFFFF
    private val MAGIC = byteArrayOf('B'.code.toByte(), 'N'.code.toByte(), 1)
    const val SLOT_SECONDS = 15 * 60L
    private const val TOKEN_BYTES = 8

    private val handler by lazy { Handler(Looper.getMainLooper()) }
    private var callback: AdvertiseCallback? = null
    private var key: ByteArray? = null
    private var appContext: Context? = null
    private val rotate = Runnable { advertise() }

    fun hasPermission(context: Context): Boolean = Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
        context.checkSelfPermission(Manifest.permission.BLUETOOTH_ADVERTISE) == PackageManager.PERMISSION_GRANTED

    /** Starts (or restarts with a new key) the signal; does nothing without permission or Bluetooth. */
    fun start(context: Context, trustKeyHex: String) {
        appContext = context.applicationContext
        key = runCatching { trustKeyHex.chunked(2).map { it.toInt(16).toByte() }.toByteArray() }.getOrNull() ?: return
        advertise()
    }

    fun stop() {
        handler.removeCallbacks(rotate)
        stopAdvertising()
        key = null
    }

    @SuppressLint("MissingPermission") // Checked in hasPermission.
    private fun advertise() {
        val context = appContext ?: return
        val key = key ?: return
        handler.removeCallbacks(rotate)
        stopAdvertising()
        if (!hasPermission(context)) return
        val advertiser = context.getSystemService(BluetoothManager::class.java)?.adapter?.takeIf { it.isEnabled }?.bluetoothLeAdvertiser
        if (advertiser != null) {
            val now = System.currentTimeMillis() / 1000
            val settings = AdvertiseSettings.Builder()
                .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_BALANCED)
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
                .setConnectable(false)
                .build()
            val data = AdvertiseData.Builder().addManufacturerData(COMPANY_ID, payload(key, now / SLOT_SECONDS)).build()
            val started = object : AdvertiseCallback() {
                override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
                    Log.i(TAG, "Signal on for slot ${now / SLOT_SECONDS}")
                }

                override fun onStartFailure(errorCode: Int) {
                    Log.w(TAG, "Advertising failed: $errorCode")
                    LinkDiagnostics.record("nearby.failed", "code $errorCode")
                }
            }
            runCatching { advertiser.startAdvertising(settings, data, started) }
                .onSuccess { callback = started }
                .onFailure { Log.w(TAG, "Advertising refused", it) }
        }
        // A new code each quarter hour; and Bluetooth turned on later gets the signal going then.
        val untilNextSlot = (SLOT_SECONDS - System.currentTimeMillis() / 1000 % SLOT_SECONDS) * 1000 + 1_000
        handler.postDelayed(rotate, untilNextSlot)
    }

    @SuppressLint("MissingPermission")
    private fun stopAdvertising() {
        val context = appContext ?: return
        val active = callback ?: return
        callback = null
        runCatching { context.getSystemService(BluetoothManager::class.java)?.adapter?.bluetoothLeAdvertiser?.stopAdvertising(active) }
    }

    /** "BN", version 1, then the first 8 bytes of HMAC-SHA256(key, "baton-near:<slot>"). */
    fun payload(key: ByteArray, slot: Long): ByteArray {
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }
        return MAGIC + mac.doFinal("baton-near:$slot".toByteArray()).copyOf(TOKEN_BYTES)
    }
}
