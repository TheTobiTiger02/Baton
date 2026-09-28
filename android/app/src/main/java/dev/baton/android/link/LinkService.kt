package dev.baton.android.link

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.protocol.DeviceForgetPayload
import dev.baton.android.protocol.DeviceInfoPayload
import dev.baton.android.protocol.Envelope
import dev.baton.android.protocol.ErrorPayload
import dev.baton.android.protocol.HeartbeatPayload
import dev.baton.android.protocol.HostEndpointsPayload
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.PairConfirmPayload
import dev.baton.android.protocol.PairHelloPayload
import dev.baton.android.protocol.PairingLink
import dev.baton.android.protocol.PeersPayload
import dev.baton.android.protocol.SessionAuthenticatePayload
import dev.baton.android.protocol.SessionChallengePayload
import dev.baton.android.protocol.SessionReadyPayload
import dev.baton.android.protocol.Wire
import dev.baton.android.protocol.Wire.read
import dev.baton.android.remote.PcRemote
import dev.baton.android.apps.AppCatalog
import dev.baton.android.stream.MediaChannel
import dev.baton.android.ui.BatonNotifications
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.select

/**
 * Keeps this phone connected to its paired PC: finds it, connects over pinned WSS, proves the
 * trust key, keeps the socket alive with heartbeats, and reconnects with jittered backoff after
 * any network change. Runs as a connected-device foreground service so Android leaves it alone.
 */
class LinkService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var trust: TrustStore
    private lateinit var endpoints: EndpointStore
    private val client = LanClient()

    private var pairing: PairingLink? = null
    private var retryIndex = 0
    private var connecting = false
    private var stopped = false
    private var lastInboundAt = 0L
    private var currentEndpoint: EndpointCandidate? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val retry = Runnable { connect() }
    private val heartbeat = object : Runnable {
        override fun run() {
            if (!Link.isReady) return
            if (System.currentTimeMillis() - lastInboundAt > STALE_AFTER_MS) {
                Log.i(TAG, "No traffic from the PC for a minute; reconnecting.")
                dropAndReconnect("The PC stopped answering. Reconnecting…")
                return
            }
            Link.send(MessageTypes.HEARTBEAT_PING, HeartbeatPayload())
            main.postDelayed(this, HEARTBEAT_MS)
        }
    }

    private val network = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            main.post {
                if (!Link.isReady && trust.isPaired) {
                    retryIndex = 0
                    scheduleConnect(immediate = true)
                }
            }
        }

        override fun onLost(network: Network) {
            main.post { if (Link.isReady) dropAndReconnect("Wi-Fi lost. Reconnecting when it's back…") }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        trust = TrustStore(this)
        endpoints = EndpointStore(this)
        Link.client = client
        Link.deviceId = trust.deviceId
        startInForeground()
        runCatching { getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(network) }
        HandoffEngine.start(this)
        PcRemote.start(this)
        dev.baton.android.mirror.ShizukuAccess.init(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopped = true
                client.disconnect("Stopped")
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_PAIR -> {
                val link = intent.getStringExtra(EXTRA_LINK)?.let(PairingLink::parse)
                if (link != null) {
                    pairing = link
                    endpoints.replaceAnnounced(link.hostId, link.endpoints)
                    client.cancel()
                    retryIndex = 0
                    connecting = false
                }
            }
            ACTION_FORGET -> {
                Link.send(MessageTypes.DEVICE_FORGET, DeviceForgetPayload("Forgotten on the phone."))
                main.postDelayed({ forgetLocally() }, 500)
                return START_STICKY
            }
        }

        stopped = false
        if (!Link.isReady && !connecting) scheduleConnect(immediate = true)
        return START_STICKY
    }

    override fun onDestroy() {
        main.removeCallbacksAndMessages(null)
        runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(network) }
        client.disconnect("Service stopped")
        HandoffEngine.stop()
        PcRemote.stop()
        releaseLocks()
        scope.cancel()
        Link.setStatus(LinkStatus(if (trust.isPaired) LinkPhase.Offline else LinkPhase.Unpaired, "Stopped"))
        super.onDestroy()
    }

    private fun connect() {
        if (stopped || connecting) return
        val link = pairing
        val host = trust.host
        val hostId = link?.hostId ?: host?.hostId
        val fingerprint = link?.certificateFingerprint ?: host?.certificateFingerprint
        if (hostId == null || fingerprint == null) {
            Link.setStatus(LinkStatus(LinkPhase.Unpaired))
            return
        }

        Link.hostId = hostId
        connecting = true
        setStatus(if (link != null) LinkPhase.Pairing else LinkPhase.Searching,
            if (link != null) "Pairing with ${link.pcName}…" else "Looking for ${host?.pcName ?: "your PC"}…")
        scope.launch {
            val endpoint = findPc(hostId, fingerprint)
            connecting = false
            if (stopped) return@launch
            if (endpoint == null && link != null) {
                pairing = null
                Link.setStatus(LinkStatus(if (trust.isPaired) LinkPhase.Offline else LinkPhase.Unpaired,
                    "Couldn't reach ${link.pcName}. Make sure both devices are on the same Wi-Fi and Baton is open on the PC."))
                return@launch
            }
            if (endpoint == null) {
                scheduleConnect(detail = "${host?.pcName ?: link?.pcName ?: "Your PC"} isn't reachable. Is Baton running on it?")
                return@launch
            }

            currentEndpoint = endpoint
            Link.pcAddress = endpoint.host
            Link.pcFingerprint = fingerprint
            if (link == null) setStatus(LinkPhase.Connecting, "Connecting to ${host?.pcName}…")
            connecting = true
            client.connect("wss://${endpoint.host}:${endpoint.port}/ws/v1/session", fingerprint, object : LanClient.Listener {
                override fun onStatus(status: String) = Unit
                override fun onMessage(message: String) {
                    main.post { this@LinkService.onMessage(message) }
                }

                override fun onClosed() {
                    main.post { this@LinkService.onClosed() }
                }
            })
        }
    }

    /**
     * Discovery and a race across every remembered address run together; the first that proves the
     * pinned certificate wins. Discovery only helps on the same Wi-Fi; remembered addresses also
     * cover networks where broadcasts are blocked.
     */
    private suspend fun findPc(hostId: String, fingerprint: String): EndpointCandidate? {
        val remembered = endpoints.ordered(hostId)
        val race = scope.async(Dispatchers.IO) {
            MultiPathConnector.race(remembered, fingerprint) { endpoints.recordFailure(it.host, it.port) }?.candidate
        }
        val discovery = scope.async(Dispatchers.IO) {
            LanDiscovery.discover().firstOrNull { it.hostId == hostId && it.fingerprint.equals(fingerprint, ignoreCase = true) }
                ?.let { EndpointCandidate(it.address, it.port, "lan", 5) }
        }

        // Whichever finds the PC first wins; if that one comes back empty, wait for the other.
        val first = select<EndpointCandidate?> {
            race.onAwait { it }
            discovery.onAwait { it }
        }
        val found = first ?: if (race.isCompleted) discovery.await() else race.await()
        race.cancel()
        discovery.cancel()
        if (found != null) endpoints.remember(hostId, found.host, found.port)
        return found
    }

    private fun onMessage(text: String) {
        lastInboundAt = System.currentTimeMillis()
        val envelope = runCatching { Wire.decode(text) }.getOrElse {
            Log.w(TAG, "Dropping an unreadable message: ${it.message}")
            return
        }

        runCatching { route(envelope) }.onFailure { Log.w(TAG, "Failed to handle ${envelope.type}", it) }
    }

    private fun route(envelope: Envelope) {
        when (envelope.type) {
            MessageTypes.SESSION_CHALLENGE -> onChallenge(envelope.read())
            MessageTypes.PAIR_CONFIRM -> onPairConfirm(envelope.read())
            MessageTypes.SESSION_READY -> onReady(envelope.read())
            MessageTypes.HOST_ENDPOINTS -> {
                val payload = envelope.read<HostEndpointsPayload>()
                endpoints.replaceAnnounced(payload.hostId, payload.endpoints)
            }
            MessageTypes.HEARTBEAT_PING -> Link.send(MessageTypes.HEARTBEAT_PONG, HeartbeatPayload())
            MessageTypes.HEARTBEAT_PONG -> Unit
            MessageTypes.PEERS -> {
                Link.setPeers(envelope.read<PeersPayload>().peers)
                BatonNotifications.updateLink(this)
            }
            MessageTypes.DEVICE_FORGOTTEN -> forgetLocally()
            MessageTypes.ERROR -> onError(envelope.read())
            MessageTypes.MEDIA_CHANNEL -> openMediaChannel(envelope.read())
            else -> HandoffEngine.onMessage(envelope)
        }
    }

    private fun onChallenge(challenge: SessionChallengePayload) {
        connecting = false
        val link = pairing
        if (link != null) {
            client.send(Wire.encode(MessageTypes.PAIR_HELLO, trust.deviceId,
                PairHelloPayload(link.code, trust.deviceId, trust.displayName, trust.model, link.hostId)))
            return
        }

        val host = trust.host ?: return
        if (challenge.hostId != host.hostId) {
            dropAndReconnect("Reached a different PC. Retrying…")
            return
        }
        val proof = SessionAuthentication.challengeProof(host.trustKey, challenge.challengeId, challenge.nonce, trust.deviceId, host.hostId)
        client.send(Wire.encode(MessageTypes.SESSION_AUTHENTICATE, trust.deviceId,
            SessionAuthenticatePayload(challenge.challengeId, trust.deviceId, proof)))
    }

    private fun onPairConfirm(confirm: PairConfirmPayload) {
        val link = pairing ?: return
        pairing = null
        if (!confirm.accepted || confirm.trustKey.isNullOrBlank()) {
            client.disconnect("Pairing rejected")
            Link.setStatus(LinkStatus(if (trust.isPaired) LinkPhase.Offline else LinkPhase.Unpaired,
                confirm.reason ?: "The PC rejected the pairing."))
            return
        }

        // The pin comes from the QR code (or discovery), never from this message: a PC that
        // could change it here would not need to be trusted in the first place.
        trust.save(TrustedHost(link.hostId, confirm.pcName, link.certificateFingerprint, confirm.trustKey))
        currentEndpoint?.let { endpoints.remember(link.hostId, it.host, it.port) }
        AutoConnectReceiver.enable(this)
    }

    private fun onReady(ready: SessionReadyPayload) {
        retryIndex = 0
        connecting = false
        trust.updatePcName(ready.pcName)
        currentEndpoint?.let { endpoints.recordSuccess(it.host, it.port) }
        acquireLocks()
        setStatus(LinkPhase.Ready, "Connected to ${ready.pcName}")
        val screen = resources.displayMetrics.let { minOf(it.widthPixels, it.heightPixels) to maxOf(it.widthPixels, it.heightPixels) }
        val real = runCatching {
            getSystemService(android.view.WindowManager::class.java).maximumWindowMetrics.bounds
                .let { minOf(it.width(), it.height()) to maxOf(it.width(), it.height()) }
        }.getOrDefault(screen)
        Link.send(MessageTypes.DEVICE_INFO, DeviceInfoPayload(trust.displayName, trust.model, screenWidth = real.first, screenHeight = real.second))
        // One media socket for the whole session, so streams start without a handshake.
        Link.send(MessageTypes.MEDIA_CHANNEL, kotlinx.serialization.json.JsonObject(emptyMap()))
        main.removeCallbacks(heartbeat)
        main.postDelayed(heartbeat, HEARTBEAT_MS)
        HandoffEngine.onReady()
        AppCatalog.publish(this)
        AppCatalog.watch(this)
    }

    private fun onError(error: ErrorPayload) {
        Log.w(TAG, "PC reported ${error.code}: ${error.message}")
        if (error.code == "authentication_rejected") {
            client.disconnect("Rejected")
            setStatus(LinkPhase.Offline, "The PC didn't accept this phone. Pair again if it keeps happening.")
        }
    }

    private fun openMediaChannel(payload: dev.baton.android.protocol.MediaChannelPayload) {
        MediaChannel.open(Link.pcAddress, Link.pcFingerprint, payload) {
            // Dropped while the session lives on (a Wi-Fi blip): ask for a new one.
            main.postDelayed({
                if (Link.isReady) Link.send(MessageTypes.MEDIA_CHANNEL, kotlinx.serialization.json.JsonObject(emptyMap()))
            }, 1_000)
        }
    }

    private fun onClosed() {
        MediaChannel.close()
        connecting = false
        main.removeCallbacks(heartbeat)
        releaseLocks()
        Link.setPeers(emptyList())
        if (!stopped && (trust.isPaired || pairing != null)) {
            scheduleConnect(detail = "Connection lost. Reconnecting…")
        }
    }

    private fun dropAndReconnect(detail: String) {
        client.cancel()
        onClosed()
        setStatus(LinkPhase.Offline, detail)
    }

    private fun forgetLocally() {
        pairing = null
        trust.forget()
        client.disconnect("Forgotten")
        Link.setPeers(emptyList())
        Link.setStatus(LinkStatus(LinkPhase.Unpaired, "This phone is no longer paired."))
        AutoConnectReceiver.disable(this)
        stopSelf()
    }

    private fun scheduleConnect(immediate: Boolean = false, detail: String? = null) {
        if (stopped) return
        main.removeCallbacks(retry)
        val delay = if (immediate) 0L else ReconnectPolicy.fullJitterDelayMs(retryIndex).also {
            if (retryIndex < ReconnectPolicy.maxAttemptIndex) retryIndex++
        }
        if (!immediate) setStatus(LinkPhase.Offline, detail ?: "Reconnecting…")
        main.postDelayed(retry, delay)
    }

    private fun setStatus(phase: LinkPhase, detail: String) {
        Link.setStatus(LinkStatus(phase, detail))
        BatonNotifications.updateLink(this)
    }

    private fun startInForeground() {
        startForeground(
            BatonNotifications.LINK_ID,
            BatonNotifications.link(this),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        )
    }

    private fun acquireLocks() {
        runCatching {
            if (wakeLock == null) {
                wakeLock = getSystemService(PowerManager::class.java)
                    .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Baton::Link").apply { setReferenceCounted(false) }
            }
            // Held for exactly the socket's lifetime; released when it closes.
            @Suppress("WakelockTimeout")
            wakeLock?.acquire()
            if (wifiLock == null) {
                @Suppress("DEPRECATION")
                wifiLock = applicationContext.getSystemService(WifiManager::class.java)
                    .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "Baton::Link").apply { setReferenceCounted(false) }
            }
            wifiLock?.acquire()
        }
    }

    private fun releaseLocks() {
        runCatching { wakeLock?.takeIf { it.isHeld }?.release() }
        runCatching { wifiLock?.takeIf { it.isHeld }?.release() }
    }

    companion object {
        private const val TAG = "BatonLink"
        private const val HEARTBEAT_MS = 20_000L
        private const val STALE_AFTER_MS = 60_000L
        const val ACTION_CONNECT = "dev.baton.android.CONNECT"
        const val ACTION_PAIR = "dev.baton.android.PAIR"
        const val ACTION_FORGET = "dev.baton.android.FORGET"
        const val ACTION_STOP = "dev.baton.android.STOP"
        const val EXTRA_LINK = "link"

        fun start(context: Context) {
            context.startForegroundService(Intent(context, LinkService::class.java).setAction(ACTION_CONNECT))
        }

        fun pair(context: Context, link: PairingLink, raw: String) {
            Link.setStatus(LinkStatus(LinkPhase.Pairing, "Pairing with ${link.pcName}…"))
            context.startForegroundService(Intent(context, LinkService::class.java).setAction(ACTION_PAIR).putExtra(EXTRA_LINK, raw))
        }

        fun forget(context: Context) {
            context.startService(Intent(context, LinkService::class.java).setAction(ACTION_FORGET))
        }
    }
}
