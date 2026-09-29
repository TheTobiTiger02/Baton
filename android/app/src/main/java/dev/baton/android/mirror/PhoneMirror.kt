package dev.baton.android.mirror

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.content.res.Configuration
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import android.view.View
import android.view.WindowManager
import dev.baton.android.BatonSettings
import dev.baton.android.R
import dev.baton.android.handoff.BatonAccessibilityService
import dev.baton.android.link.Link
import dev.baton.android.protocol.HandoffResultPayload
import dev.baton.android.protocol.HandoffStatus
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.stream.ControlMessage
import dev.baton.android.stream.ControlMessageCodec
import dev.baton.android.stream.MediaChannel
import dev.baton.android.stream.MediaSink
import dev.baton.android.stream.StreamProtocol
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * This phone's screen continued on the PC: captured, encoded and sent over the media channel,
 * with the PC's mouse and keyboard played back in. How Android's screen-sharing consent is got
 * depends on [BatonSettings.MirrorMode]: on every handoff (one tap on Android 14+, which offers
 * only "entire screen"), once while the capture is kept ready, or never with Shizuku.
 */
object PhoneMirror {
    private const val TAG = "BatonMirror"

    /** The session being mirrored, if any. */
    @Volatile var sessionId: String? = null
        private set
    internal var targetDeviceId: String = ""
    internal var title: String = ""
    internal var packageName: String? = null

    /** Starts mirroring [packageName]'s screen as [sessionId] to [targetDeviceId], asking for consent when needed. */
    fun start(context: Context, sessionId: String, targetDeviceId: String, title: String, packageName: String?) {
        stop(context)
        this.sessionId = sessionId
        this.targetDeviceId = targetDeviceId
        this.title = title
        this.packageName = packageName
        if (MirrorService.ready && BatonSettings.mirrorMode(context) == BatonSettings.MirrorMode.KeepReady) {
            // Kept ready: the capture Android already allowed carries on, no prompt.
            context.startService(Intent(context, MirrorService::class.java).setAction(MirrorService.ACTION_SESSION))
        } else {
            dev.baton.android.handoff.Launcher.start(context, Intent(context, MirrorConsentActivity::class.java), "Show $title on your PC")
        }
    }

    fun stop(context: Context) {
        if (sessionId == null) return
        context.startService(Intent(context, MirrorService::class.java).setAction(MirrorService.ACTION_STOP))
    }

    /** Ends a kept-ready capture (the mode changed); a running session still finishes normally. */
    fun release(context: Context) {
        if (MirrorService.ready) context.startService(Intent(context, MirrorService::class.java).setAction(MirrorService.ACTION_RELEASE))
    }

    /** Whether the PC can control this phone: accessibility or the Shizuku helper plays its input in. */
    internal val inputReady: Boolean get() = BatonAccessibilityService.instance != null || ShizukuAccess.shell != null

    /** [id] is over; a session started since keeps its place. */
    internal fun ended(id: String?) {
        if (sessionId == id) sessionId = null
    }

    /** Full display size, including what is under the system bars: that is what gets captured. */
    internal fun displaySize(context: Context): Pair<Int, Int> =
        context.getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds.let { it.width() to it.height() }

    /** Encoded size: the display, scaled so the long edge is at most 1920, even on both sides. */
    fun encodedSize(width: Int, height: Int): Pair<Int, Int> {
        val scale = minOf(1.0, 1920.0 / max(width, height))
        fun even(value: Double) = (value.roundToInt() / 2) * 2
        return even(width * scale) to even(height * scale)
    }

    /** [now] writes on the calling thread, in order with video written there; never on the main thread. */
    internal fun sendMeta(type: String, width: Int = 0, height: Int = 0, reason: String? = null, now: Boolean = false, session: String? = sessionId) {
        val id = session ?: return
        val json = buildJsonObject {
            put("type", JsonPrimitive(type))
            put("session", JsonPrimitive(id))
            if (type == "format") {
                put("width", JsonPrimitive(width))
                put("height", JsonPrimitive(height))
                put("title", JsonPrimitive(title))
            }
            if (type == "input") put("ready", JsonPrimitive(inputReady))
            reason?.let { put("reason", JsonPrimitive(it)) }
        }
        val bytes = json.toString().toByteArray()
        if (now) MediaChannel.sendNow(StreamProtocol.CHANNEL_META, bytes, 0, 0) else MediaChannel.send(StreamProtocol.CHANNEL_META, bytes)
    }

    internal fun log(message: String) = Log.i(TAG, message)

    /** Tells the PC whether it can control this phone (Baton's accessibility service is on). */
    fun inputChanged() {
        if (sessionId != null) sendMeta("input")
    }
}

/** Shows Android's screen-sharing consent, then hands the grant to [MirrorService]. */
class MirrorConsentActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val manager = getSystemService(MediaProjectionManager::class.java)
        val intent = if (Build.VERSION.SDK_INT >= 34) {
            // One tap: the dialog offers only the entire screen, which is what the PC shows.
            manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
        } else {
            manager.createScreenCaptureIntent()
        }
        @Suppress("DEPRECATION")
        startActivityForResult(intent, REQUEST)
    }

    @Deprecated("The platform Activity has no result API.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode == RESULT_OK && data != null) {
            startForegroundService(Intent(this, MirrorService::class.java)
                .setAction(MirrorService.ACTION_START)
                .putExtra(MirrorService.EXTRA_CODE, resultCode)
                .putExtra(MirrorService.EXTRA_DATA, data))
        } else {
            PhoneMirror.log("Screen sharing declined")
            PhoneMirror.sendMeta("end", reason = "Screen sharing was declined on the phone.")
            PhoneMirror.sessionId?.let {
                dev.baton.android.handoff.HandoffEngine.fail(it, "Screen sharing was declined on the phone.")
                Link.send(MessageTypes.HANDOFF_RESULT, HandoffResultPayload(it, Link.deviceId, PhoneMirror.targetDeviceId,
                    HandoffStatus.Failed, "Screen sharing was declined on the phone."))
            }
            PhoneMirror.ended(PhoneMirror.sessionId)
        }
        finish()
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    private companion object {
        const val REQUEST = 1
    }
}

/**
 * Captures the screen (and what the phone plays) while the PC shows it. A foreground service
 * because Android requires one for screen capture; it also keeps the screen on, since a phone
 * that locks while the PC drives it would show the PC a lock screen.
 *
 * Two lifetimes: the capture Android allowed (projection and its one virtual display), and each
 * session the PC shows. In [BatonSettings.MirrorMode.KeepReady] the capture outlives a session:
 * the virtual display keeps no surface in between, so nothing is drawn or encoded, and the next
 * handoff starts without asking. Otherwise the capture ends with the session.
 */
class MirrorService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var codec: MediaCodec? = null
    private val bitrate = MirrorBitrate()
    private var mutedAudio = false
    private var drain: Thread? = null
    private var audio: Thread? = null
    private var awake: View? = null
    private var session: String? = null
    @Volatile private var running = false
    private var size = 0 to 0
    private val idleTimeout = Runnable { release() }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> start(intent)
            ACTION_SESSION -> if (projection != null) startSession() else PhoneMirror.sessionId?.let { declined(it, "Screen sharing stopped on the phone.") }
            ACTION_STOP -> if (running) {
                endSession("Stopped")
                keepOrRelease()
            }
            ACTION_RELEASE -> if (!running) release()
        }
        if (projection == null) stopSelf()
        return START_NOT_STICKY
    }

    /** A new grant from the consent screen: replaces any capture kept so far. */
    private fun start(intent: Intent) {
        ensureChannel()
        startForeground(NOTIFICATION_ID, sessionNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        if (running) endSession("Replaced")
        releaseProjection()

        @Suppress("DEPRECATION")
        val data = intent.getParcelableExtra<Intent>(EXTRA_DATA)
        val granted = data?.let { getSystemService(MediaProjectionManager::class.java).getMediaProjection(intent.getIntExtra(EXTRA_CODE, 0), it) }
        if (granted == null) {
            PhoneMirror.sessionId?.let { declined(it, "Android did not grant screen capture.") }
            release()
            return
        }
        projection = granted
        ready = true
        granted.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                main.post {
                    if (projection !== granted) return@post
                    // Android ended the capture (the cast icon, a reboot, some versions on lock).
                    if (running) endSession("Screen sharing stopped on the phone.")
                    release()
                }
            }
        }, main)
        startSession()
    }

    private fun startSession() {
        val projection = projection ?: return
        main.removeCallbacks(idleTimeout)
        if (running) endSession("Replaced")
        session = PhoneMirror.sessionId
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, sessionNotification())

        running = true
        val phoneInput = PhoneInput(this)
        MediaChannel.controlSink = MediaSink { _, payload ->
            val message = runCatching { ControlMessageCodec.decode(payload) }.getOrNull() ?: return@MediaSink
            if (message is ControlMessage.KeyframeRequest) requestKeyframe() else main.post { phoneInput.apply(message) }
        }
        startVideo()
        startAudio(projection)
        keepScreenOn()
        dev.baton.android.stream.ClipboardSync.start(this)
        PhoneMirror.sendMeta("input", session = session)
        if (!PhoneMirror.inputReady) {
            // Without it the PC can watch but not touch: say so here, where it can be fixed.
            android.widget.Toast.makeText(this, "Turn on Baton in Accessibility to control this phone from your PC", android.widget.Toast.LENGTH_LONG).show()
            runCatching {
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
        // The consent prompt came up over the app; put the app back in front, where the PC expects it.
        PhoneMirror.packageName?.let { app ->
            packageManager.getLaunchIntentForPackage(app)?.let {
                dev.baton.android.handoff.Launcher.start(this, it.addFlags(Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED), PhoneMirror.title)
            }
        }
        PhoneMirror.log("Mirroring ${size.first}x${size.second} to the PC")
    }

    /** The PC stopped showing this phone. The capture is left for the caller to keep or release. */
    private fun endSession(reason: String) {
        if (running) {
            PhoneMirror.log("Mirror ended: $reason")
            PhoneMirror.sendMeta("end", reason = reason, session = session)
        }
        if (running) dev.baton.android.stream.ClipboardSync.stop()
        running = false
        MediaChannel.controlSink = null
        stopVideo()
        audio?.join(200)
        if (mutedAudio) {
            // Back to the phone: its sound too.
            runCatching {
                getSystemService(android.media.AudioManager::class.java)
                    ?.adjustStreamVolume(android.media.AudioManager.STREAM_MUSIC, android.media.AudioManager.ADJUST_UNMUTE, 0)
            }
            mutedAudio = false
        }
        audio = null
        awake?.let { runCatching { getSystemService(WindowManager::class.java).removeView(it) } }
        awake = null
        PhoneMirror.ended(session)
        session = null
    }

    /** After a session: the capture stays ready in the KeepReady mode, and ends otherwise. */
    private fun keepOrRelease() {
        if (projection != null && BatonSettings.mirrorMode(this) == BatonSettings.MirrorMode.KeepReady) {
            getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, readyNotification())
            main.removeCallbacks(idleTimeout)
            main.postDelayed(idleTimeout, KEEP_READY_MS)
        } else {
            release()
        }
    }

    /** Ends the capture, and with it the service. */
    private fun release() {
        main.removeCallbacks(idleTimeout)
        releaseProjection()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun releaseProjection() {
        ready = false
        runCatching { display?.release() }
        display = null
        val ended = projection
        projection = null
        runCatching { ended?.stop() }
    }

    private fun declined(id: String, reason: String) {
        PhoneMirror.sendMeta("end", reason = reason, session = id)
        Link.send(MessageTypes.HANDOFF_RESULT, HandoffResultPayload(id, Link.deviceId, PhoneMirror.targetDeviceId, HandoffStatus.Failed, reason))
        dev.baton.android.handoff.HandoffEngine.fail(id, reason)
        PhoneMirror.ended(id)
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        val (width, height) = PhoneMirror.displaySize(this)
        if (running && PhoneMirror.encodedSize(width, height) != size) {
            // Rotated: a new encoder at the new shape; the projection and the channel stay.
            stopVideo()
            startVideo()
        }
    }

    private fun startVideo() {
        val (displayWidth, displayHeight) = PhoneMirror.displaySize(this)
        val (width, height) = PhoneMirror.encodedSize(displayWidth, displayHeight)
        size = width to height
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate.bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, 60)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10)
            setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
            // A still screen still produces frames, so the PC never mistakes idle for frozen.
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000)
            setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger(MediaFormat.KEY_LATENCY, 1)
        }
        val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        val surface = encoder.createInputSurface()
        encoder.start()
        codec = encoder
        // A projection may create only one virtual display (Android 14+): later sessions and
        // rotations resize it and give it the new encoder's surface.
        val existing = display
        if (existing == null) {
            display = projection!!.createVirtualDisplay("Baton mirror", width, height, resources.displayMetrics.densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, surface, null, null)
        } else {
            existing.resize(width, height, resources.displayMetrics.densityDpi)
            existing.surface = surface
        }
        drain = Thread({ drain(encoder) }, "Baton mirror encoder").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    private fun drain(encoder: MediaCodec) {
        // Before any video, and on this thread, so the PC knows the shape before the first frame.
        PhoneMirror.sendMeta("format", size.first, size.second, now = true, session = session)
        val info = MediaCodec.BufferInfo()
        val scratch = ByteArray(2 * 1024 * 1024)
        var behind = false
        while (running && codec === encoder) {
            val index = try {
                encoder.dequeueOutputBuffer(info, 100_000)
            } catch (e: IllegalStateException) {
                break
            }
            if (index < 0) continue
            val buffer = encoder.getOutputBuffer(index)
            if (buffer != null && info.size > 0) {
                var flags = 0
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) flags = flags or StreamProtocol.FLAG_CODEC_CONFIG
                if (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0) flags = flags or StreamProtocol.FLAG_KEYFRAME
                // Behind, frames that waited meanwhile are stale: skip to the next keyframe instead
                // of sending them late, so the PC shows the phone as it is now.
                val key = flags != 0
                if (behind && !key) {
                    runCatching { encoder.releaseOutputBuffer(index, false) }
                    continue
                }
                behind = false
                val bytes = if (info.size <= scratch.size) scratch else ByteArray(info.size)
                buffer.position(info.offset)
                buffer.get(bytes, 0, info.size)
                val started = SystemClock.elapsedRealtime()
                MediaChannel.sendNow(StreamProtocol.CHANNEL_VIDEO, bytes, flags, info.presentationTimeUs, 0, info.size)
                if (SystemClock.elapsedRealtime() - started > SLOW_WRITE_MS) {
                    behind = true
                    requestKeyframe()
                    bitrate.dropped()?.let { setBitrate(encoder, it) }
                } else {
                    bitrate.tick()?.let { setBitrate(encoder, it) }
                }
            }
            runCatching { encoder.releaseOutputBuffer(index, false) }
        }
    }

    private fun setBitrate(encoder: MediaCodec, bitsPerSecond: Int) {
        runCatching { encoder.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bitsPerSecond) }) }
        PhoneMirror.log("Mirror bitrate ${bitsPerSecond / 1_000_000.0} Mbps")
    }

    private fun requestKeyframe() {
        runCatching { codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }
    }

    /** Stops encoding; the virtual display is kept, without a surface, so nothing is drawn. */
    private fun stopVideo() {
        val encoder = codec ?: return
        codec = null
        runCatching { display?.surface = null }
        drain?.join(500)
        drain = null
        runCatching { encoder.stop() }
        runCatching { encoder.release() }
    }

    /** What the phone plays, as 10 ms chunks of 48 kHz 16-bit stereo PCM. Silent without the microphone permission. */
    @SuppressLint("MissingPermission")
    private fun startAudio(projection: MediaProjection) {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            PhoneMirror.log("No microphone permission: mirroring without sound")
            return
        }
        val record = runCatching {
            AudioRecord.Builder()
                .setAudioFormat(AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(48_000)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO).build())
                .setAudioPlaybackCaptureConfig(AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .build())
                .setBufferSizeInBytes(CHUNK_BYTES * 8)
                .build()
        }.getOrNull() ?: return
        // The sound continues on the PC only. Playback capture takes it before the volume, so
        // muting the phone's media stream doesn't quiet the PC.
        getSystemService(android.media.AudioManager::class.java)?.let { manager ->
            runCatching { manager.adjustStreamVolume(android.media.AudioManager.STREAM_MUSIC, android.media.AudioManager.ADJUST_MUTE, 0) }
            mutedAudio = true
        }
        audio = Thread({
            val chunk = ByteArray(CHUNK_BYTES)
            try {
                record.startRecording()
                while (running) {
                    var filled = 0
                    while (filled < CHUNK_BYTES && running) {
                        val read = record.read(chunk, filled, CHUNK_BYTES - filled)
                        if (read < 0) return@Thread
                        filled += read
                    }
                    MediaChannel.sendNow(StreamProtocol.CHANNEL_AUDIO, chunk, 0, SystemClock.elapsedRealtimeNanos() / 1000, 0, filled)
                }
            } finally {
                runCatching { record.stop() }
                record.release()
            }
        }, "Baton mirror audio").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    /** A 1-pixel invisible overlay that keeps the display on while the PC is using the phone. */
    private fun keepScreenOn() {
        if (!Settings.canDrawOverlays(this)) return
        val view = View(this)
        val params = WindowManager.LayoutParams(1, 1, WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON, PixelFormat.TRANSLUCENT)
        runCatching {
            getSystemService(WindowManager::class.java).addView(view, params)
            awake = view
        }
    }

    override fun onDestroy() {
        if (running) endSession("The phone stopped sharing.")
        main.removeCallbacks(idleTimeout)
        releaseProjection()
        super.onDestroy()
    }

    private fun sessionNotification(): Notification = Notification.Builder(this, CHANNEL)
        .setSmallIcon(R.drawable.ic_baton)
        .setContentTitle("Showing this phone on your PC")
        .setContentText(PhoneMirror.title)
        .setOngoing(true)
        .build()

    private fun readyNotification(): Notification {
        val stop = PendingIntent.getService(this, 0, Intent(this, MirrorService::class.java).setAction(ACTION_RELEASE),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle("Screen sharing ready for your PC")
            .setContentText("Your PC can show this phone without asking. Nothing is shared until it does.")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }

    private fun ensureChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Showing on your PC", NotificationManager.IMPORTANCE_LOW))
        }
    }

    companion object {
        const val ACTION_START = "dev.baton.android.mirror.START"
        const val ACTION_SESSION = "dev.baton.android.mirror.SESSION"
        const val ACTION_STOP = "dev.baton.android.mirror.STOP"
        const val ACTION_RELEASE = "dev.baton.android.mirror.RELEASE"
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        private const val CHANNEL = "mirror"
        private const val NOTIFICATION_ID = 9

        /** A video write that blocks this long means the socket is full: the link can't keep up. */
        private const val SLOW_WRITE_MS = 50L
        private const val CHUNK_BYTES = 48_000 / 100 * 2 * 2

        /** A kept-ready capture gives up after this long unused, so the cast icon doesn't stay for good. */
        private const val KEEP_READY_MS = 4 * 60 * 60 * 1000L

        /** A capture exists; in the KeepReady mode a session can start on it without asking. */
        @Volatile var ready = false
            private set
    }
}
