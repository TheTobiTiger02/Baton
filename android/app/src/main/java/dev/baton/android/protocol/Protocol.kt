package dev.baton.android.protocol

import java.time.Instant
import java.time.OffsetDateTime
import java.util.UUID
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement

/**
 * Twin of `Baton.Protocol` on the PC. Field names are the camelCase JSON the PC's web serializer
 * writes; timestamps travel as ISO-8601 strings.
 */
object Wire {
    const val VERSION = 1

    val json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
        encodeDefaults = true
    }

    fun parseTime(value: String?): Long =
        value?.let { runCatching { OffsetDateTime.parse(it).toInstant().toEpochMilli() }.getOrNull() }
            ?: System.currentTimeMillis()

    fun time(epochMs: Long = System.currentTimeMillis()): String = Instant.ofEpochMilli(epochMs).toString()

    fun newId(): String = UUID.randomUUID().toString().replace("-", "")

    inline fun <reified T> encode(type: String, deviceId: String, payload: T): String =
        json.encodeToString(Envelope.serializer(), Envelope(
            type = type,
            deviceId = deviceId,
            payload = json.encodeToJsonElement(payload)
        ))

    fun decode(text: String): Envelope = json.decodeFromString(Envelope.serializer(), text)

    inline fun <reified T> Envelope.read(): T = json.decodeFromJsonElement(payload)
}

object MessageTypes {
    const val SESSION_CHALLENGE = "session.challenge"
    const val SESSION_AUTHENTICATE = "session.authenticate"
    const val SESSION_READY = "session.ready"
    const val PAIR_HELLO = "pair.hello"
    const val PAIR_CONFIRM = "pair.confirm"
    const val HEARTBEAT_PING = "heartbeat.ping"
    const val HEARTBEAT_PONG = "heartbeat.pong"
    const val HOST_ENDPOINTS = "host.endpoints"
    const val DEVICE_INFO = "device.info"
    const val DEVICE_FORGET = "device.forget"
    const val DEVICE_FORGOTTEN = "device.forgotten"
    const val PEERS = "peers"
    const val ERROR = "error"
    const val ACTIVITY_LIST = "activity.list"
    const val HANDOFF_PULL = "handoff.pull"
    const val HANDOFF_DELIVER = "handoff.deliver"
    const val HANDOFF_RESULT = "handoff.result"
    const val MEDIA_CHANNEL = "media.channel"
    const val STREAM_CONTROL = "stream.control"
    const val MEDIA_COMMAND = "media.command"
    const val HANDOFF_OPTIONS = "handoff.options"
    const val APP_CATALOG = "apps.catalog"
    const val APP_PREFERENCES = "apps.preferences"
    const val APP_PREFERENCE_REMOVE = "apps.preference.remove"
}

object HandoffModes {
    const val AUTO = "auto"
    const val STREAM = "stream"
}

object StreamKinds {
    const val WINDOW = "window"
    const val PHONE = "phone"
}

object StreamActions {
    const val STOP = "stop"
    const val RETURN = "return"
    const val FIT = "fit"
    const val UNFIT = "unfit"
    const val FIRST_FRAME = "first-frame"
}

object MediaActions {
    const val PLAY = "play"
    const val PAUSE = "pause"
    const val TOGGLE = "toggle"
    const val SEEK = "seek"
    const val SKIP = "skip"
    const val NEXT = "next"
    const val PREVIOUS = "previous"
    const val VOLUME = "volume"
}

@Serializable
data class MediaChannelPayload(val ticket: String, val streamId: Long, val port: Int)

@Serializable
data class StreamControlPayload(val sessionId: String, val action: String, val elapsedMs: Long? = null)

@Serializable
data class MediaCommandPayload(
    val ownerDeviceId: String,
    val activityId: String,
    val action: String,
    val positionMs: Long? = null,
    val volume: Double? = null
)

@Serializable
data class Envelope(
    val v: Int = Wire.VERSION,
    val id: String = Wire.newId(),
    val type: String,
    val deviceId: String,
    val timestamp: String = Wire.time(),
    val payload: JsonElement = JsonObject(emptyMap())
)

@Serializable
data class HostEndpoint(val host: String, val port: Int, val kind: String = "lan", val preference: Int = 50)

@Serializable
data class DiscoveryResponse(
    val hostId: String,
    val pcName: String,
    val protocolVersion: Int,
    val wssPort: Int,
    val certificateFingerprint: String,
    val endpoints: List<HostEndpoint> = emptyList()
)

@Serializable
data class HostEndpointsPayload(val hostId: String, val endpoints: List<HostEndpoint>)

@Serializable
data class SessionChallengePayload(
    val challengeId: String,
    val nonce: String,
    val hostId: String,
    val pcName: String,
    val expiresAt: String
)

@Serializable
data class SessionAuthenticatePayload(val challengeId: String, val deviceId: String, val proof: String)

@Serializable
data class PairHelloPayload(
    val pairingCode: String,
    val deviceId: String,
    val displayName: String,
    val model: String,
    val hostId: String? = null
)

@Serializable
data class PairConfirmPayload(
    val accepted: Boolean,
    val deviceId: String,
    val trustKey: String? = null,
    val reason: String? = null,
    val errorCode: String? = null,
    val hostId: String,
    val pcName: String,
    val certificateFingerprint: String
)

@Serializable
data class SessionReadyPayload(
    val deviceId: String,
    val sessionId: String,
    val hostId: String,
    val pcName: String,
    val heartbeatSeconds: Int = 20,
    val staleAfterSeconds: Int = 60
)

@Serializable
data class HeartbeatPayload(val sentAt: String = Wire.time())

@Serializable
data class DeviceInfoPayload(
    val displayName: String,
    val model: String,
    val batteryLevel: Int? = null,
    val screenWidth: Int? = null,
    val screenHeight: Int? = null
)

@Serializable
data class DeviceForgetPayload(val reason: String? = null)

@Serializable
data class ErrorPayload(val code: String, val message: String, val correlationId: String? = null)

@Serializable
enum class ActivityKind {
    @SerialName("webPage") WebPage,
    @SerialName("webMedia") WebMedia,
    @SerialName("appMedia") AppMedia,
    @SerialName("localMedia") LocalMedia,
    @SerialName("windowStream") WindowStream
}

@Serializable
enum class PresenceState {
    @SerialName("active") Active,
    @SerialName("idle") Idle,
    @SerialName("locked") Locked
}

@Serializable
data class ActivityApp(val name: String, val id: String)

@Serializable
data class ActivityContent(val provider: String, val id: String? = null, val query: String? = null)

@Serializable
data class ActivityFile(val fileId: String, val name: String, val size: Long, val mime: String)

@Serializable
data class Playback(
    val positionMs: Long,
    val durationMs: Long,
    val playing: Boolean,
    val rate: Double = 1.0,
    val capturedAt: String
) {
    /** The position now: a playing snapshot keeps advancing from when it was taken. */
    fun positionAt(nowMs: Long = System.currentTimeMillis()): Long {
        if (!playing) return positionMs
        val elapsed = ((nowMs - Wire.parseTime(capturedAt)) * (if (rate <= 0) 1.0 else rate)).toLong()
        val position = positionMs + elapsed.coerceAtLeast(0)
        return if (durationMs > 0) position.coerceAtMost(durationMs) else position
    }

    companion object {
        /** Longest believable duration. Live streams and some apps report far more; the PC treats more as unknown. */
        const val MAX_PLAUSIBLE_MS = 7L * 24 * 60 * 60 * 1000

        /** [durationMs] as a media session reported it, or 0 when it cannot be a real length. */
        fun plausibleDuration(durationMs: Long): Long = if (durationMs in 1..MAX_PLAUSIBLE_MS) durationMs else 0L
    }
}

@Serializable
data class ActivityWindow(val windowToken: String, val processName: String)

@Serializable
data class Activity(
    val id: String,
    val deviceId: String,
    val kind: ActivityKind,
    val title: String,
    val app: ActivityApp,
    val updatedAt: String,
    val subtitle: String? = null,
    val artworkJpegBase64: String? = null,
    val url: String? = null,
    val content: ActivityContent? = null,
    val file: ActivityFile? = null,
    val playback: Playback? = null,
    val window: ActivityWindow? = null,
    val volume: Double? = null,
    /** Plays with sound right now; a muted autoplay video does not. Null when unknown. */
    val audible: Boolean? = null,
    /** In the window (or browser tab) the user last had in front. Null when unknown. */
    val focused: Boolean? = null,
    /** The first words at the top of a page's view: a browser opening it scrolls there. */
    val textAnchor: String? = null
)

@Serializable
data class ActivityListPayload(val activities: List<Activity>, val presence: PresenceState)

@Serializable
data class PeerInfo(
    val deviceId: String,
    val name: String,
    val kind: String,
    val online: Boolean,
    val presence: PresenceState,
    val activities: List<Activity>
)

@Serializable
data class PeersPayload(val peers: List<PeerInfo>)

@Serializable
data class HandoffPullPayload(
    val requestId: String,
    val sourceDeviceId: String,
    val targetDeviceId: String,
    val activityId: String? = null,
    val speculative: Boolean = false,
    val mode: String = HandoffModes.AUTO,
    val choice: HandoffChoice? = null
)

@Serializable
data class HandoffDeliverPayload(
    val requestId: String,
    val sourceDeviceId: String,
    val targetDeviceId: String,
    val activity: Activity,
    val stream: StreamOffer? = null,
    val choice: HandoffChoice? = null
)

/**
 * How an activity continues on the other device (twin of Baton.Protocol.HandoffChoice): the usual
 * way, a specific app there ([appId]: a package here, an AUMID/path/browser on the PC), a website
 * ([url]) or a live stream. [remember] asks the PC to keep it for this app.
 */
@Serializable
data class HandoffChoice(
    val kind: String,
    val label: String,
    val appId: String? = null,
    val url: String? = null,
    val remember: Boolean = false
)

object ChoiceKinds {
    const val DEFAULT = "default"
    const val APP = "app"
    const val WEB = "web"
    const val STREAM = "stream"
}

@Serializable
data class HandoffOptionsRequest(val requestId: String, val activity: Activity, val targetDeviceId: String)

@Serializable
data class HandoffOptionsPayload(val requestId: String, val options: List<HandoffChoice>, val remembered: HandoffChoice? = null)

@Serializable
data class InstalledApp(val id: String, val name: String)

@Serializable
data class AppCatalogPayload(val apps: List<InstalledApp>)

@Serializable
data class AppPreference(val key: String, val sourceName: String, val choice: HandoffChoice)

@Serializable
data class AppPreferencesPayload(val preferences: List<AppPreference>)

@Serializable
data class AppPreferenceRemovePayload(val key: String)

object Platforms {
    const val WINDOWS = "windows"
    const val ANDROID = "android"
}

object ChoiceKeys {
    /** Same as the PC's ChoiceKeys.For. */
    fun of(sourcePlatform: String, appId: String, targetPlatform: String) = "$sourcePlatform:${appId.lowercase()}->$targetPlatform"

    fun of(sourcePlatform: String, activity: Activity, targetPlatform: String) = of(sourcePlatform, subject(activity), targetPlatform)

    /** The site for anything in a browser (site:youtube, site:x.com), the app otherwise. Same as the PC's. */
    fun subject(activity: Activity): String =
        if (activity.kind == ActivityKind.WebMedia || activity.kind == ActivityKind.WebPage) "site:${site(activity)}" else activity.app.id

    fun site(activity: Activity): String {
        val provider = activity.content?.provider
        if (provider != null && provider !in setOf("web", "unknown", "")) return provider
        val host = runCatching { java.net.URI(activity.url ?: return "web").host }.getOrNull()
        return host?.takeIf { it.isNotEmpty() }?.removePrefix("www.")?.lowercase() ?: "web"
    }
}

/** A live stream of a PC window, ready to watch: dial [port] and present each ticket. */
@Serializable
data class StreamOffer(
    val sessionId: String,
    val width: Int,
    val height: Int,
    val title: String,
    val kind: String = StreamKinds.WINDOW
)

@Serializable
enum class HandoffStatus {
    @SerialName("opened") Opened,
    @SerialName("fallback") Fallback,
    @SerialName("failed") Failed
}

@Serializable
data class HandoffResultPayload(
    val requestId: String,
    val sourceDeviceId: String,
    val targetDeviceId: String,
    val status: HandoffStatus,
    val detail: String? = null,
    val openedMs: Long? = null,
    val firstFrameMs: Long? = null
)

/** The pairing QR code: `baton://pair?h=hostId&n=pcName&f=fingerprint&c=code&e=host:port,host:port`. */
data class PairingLink(
    val hostId: String,
    val pcName: String,
    val certificateFingerprint: String,
    val code: String,
    val endpoints: List<HostEndpoint>
) {
    fun toUri(): String {
        fun e(value: String) = java.net.URLEncoder.encode(value, "UTF-8").replace("+", "%20")
        val hosts = endpoints.joinToString(",") { "${it.host}:${it.port}" }
        return "baton://pair?h=${e(hostId)}&n=${e(pcName)}&f=${e(certificateFingerprint)}&c=${e(code)}&e=${e(hosts)}"
    }

    companion object {
        fun parse(value: String): PairingLink? {
            val uri = runCatching { java.net.URI(value.trim()) }.getOrNull() ?: return null
            if (!uri.scheme.equals("baton", ignoreCase = true) || !uri.host.equals("pair", ignoreCase = true)) return null
            val query = uri.rawQuery.orEmpty().split('&').mapNotNull {
                val parts = it.split('=', limit = 2)
                if (parts.size == 2) parts[0] to java.net.URLDecoder.decode(parts[1], "UTF-8") else null
            }.toMap()
            val hostId = query["h"].orEmpty()
            val fingerprint = query["f"].orEmpty()
            val code = query["c"].orEmpty()
            if (hostId.isBlank() || fingerprint.isBlank() || code.isBlank()) return null
            val endpoints = query["e"].orEmpty().split(',').mapNotNull { item ->
                val separator = item.lastIndexOf(':')
                val port = item.substring(separator + 1).toIntOrNull()
                if (separator > 0 && port != null) HostEndpoint(item.substring(0, separator), port, "lan", 10) else null
            }
            return PairingLink(hostId, query["n"] ?: "PC", fingerprint, code, endpoints)
        }
    }
}
