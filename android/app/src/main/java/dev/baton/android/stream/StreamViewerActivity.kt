package dev.baton.android.stream

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.text.InputType
import android.util.Base64
import android.util.Log
import android.view.Gravity
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import dev.baton.android.link.Link
import dev.baton.android.protocol.Activity as BatonActivity
import dev.baton.android.protocol.MediaActions
import dev.baton.android.protocol.MediaCommandPayload
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.StreamActions
import dev.baton.android.protocol.StreamControlPayload
import dev.baton.android.protocol.Wire
import java.lang.ref.WeakReference
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * Shows a PC window live and lets the phone drive it: real multi-touch (the PC gets genuine
 * touch input), or a trackpad with a precise cursor; a keyboard with Ctrl, Alt, Win, arrows and
 * function keys; pinch-zoom in trackpad mode; fitting the PC window to the phone; and handing the
 * window back to the PC.
 *
 * It opens the moment it is asked for, showing the activity's artwork until the first frame, and
 * takes its frames from the always-open media channel, so there is nothing to connect.
 */
class StreamViewerActivity : Activity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var root: FrameLayout
    private lateinit var stage: FrameLayout
    private lateinit var surface: SurfaceView
    private lateinit var placeholder: LinearLayout
    private lateinit var toolbar: LinearLayout
    private lateinit var keyRow: LinearLayout
    private lateinit var functionRow: LinearLayout
    private lateinit var mediaBar: LinearLayout
    private lateinit var handle: TextView
    private lateinit var keyboard: KeyCaptureView
    private lateinit var modeButton: TextView
    private lateinit var fitButton: TextView

    private var sessionId = ""
    private var ownerDeviceId = ""
    private var activityInfo: BatonActivity? = null
    private var startedAt = 0L
    private var frameWidth = 1920
    private var frameHeight = 1080
    private var trackpad = false
    private var fitted = true
    private var firstFrameShown = false
    private var closingByUser = false
    private val modifiers = mutableSetOf<Int>()

    @Volatile private var decoder: MediaCodec? = null
    @Volatile private var spareDecoder: MediaCodec? = null
    @Volatile private var decoderSurface: android.view.Surface? = null
    @Volatile private var decoderReady = false
    @Volatile private var closing = false
    private var audio: AudioTrack? = null
    private val hideToolbar = Runnable { setToolbarVisible(false) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Copies travel between this phone and the PC while its window is shown here.
        ClipboardSync.start(this)
        current = WeakReference(this)
        readIntent(intent)
        // Creating a decoder takes a while on some phones: do it while the window is still coming up.
        Thread({ spareDecoder = runCatching { MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC) }.getOrNull() }, "Baton decoder alloc").start()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.setDecorFitsSystemWindows(false)
        window.attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        setContentView(buildLayout())
        applyOrientation()
        hideSystemBars()
        setToolbarVisible(true)
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT) { handleBack() }
        }
        MediaChannel.metaSink = MediaSink { _, payload -> main.post { onMeta(String(payload)) } }
        MediaChannel.audioSink = MediaSink { _, payload -> playAudio(payload) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        val previous = sessionId
        readIntent(intent)
        if (previous != sessionId) {
            firstFrameShown = false
            placeholder.visibility = View.VISIBLE
            applyOrientation()
        }
    }

    private fun readIntent(intent: Intent) {
        sessionId = intent.getStringExtra(EXTRA_SESSION) ?: sessionId
        ownerDeviceId = intent.getStringExtra(EXTRA_OWNER) ?: ownerDeviceId
        startedAt = intent.getLongExtra(EXTRA_STARTED, SystemClock.elapsedRealtime())
        intent.getStringExtra(EXTRA_ACTIVITY)?.let { activityInfo = Wire.json.decodeFromString(BatonActivity.serializer(), it) }
        val width = intent.getIntExtra(EXTRA_WIDTH, 0)
        val height = intent.getIntExtra(EXTRA_HEIGHT, 0)
        if (width > 0 && height > 0) {
            frameWidth = width
            frameHeight = height
        } else {
            // Until the PC says, assume the phone's own landscape shape: that is what it will send.
            val metrics = resources.displayMetrics
            frameWidth = max(metrics.widthPixels, metrics.heightPixels)
            frameHeight = min(metrics.widthPixels, metrics.heightPixels)
            if (activityInfo?.window == null && activityInfo != null) Unit
        }
    }

    override fun onDestroy() {
        ClipboardSync.stop()
        if (current?.get() === this) current = null
        closing = true
        MediaChannel.attachVideo(null)
        MediaChannel.metaSink = null
        MediaChannel.audioSink = null
        releaseDecoder()
        spareDecoder?.let { runCatching { it.release() } }
        runCatching { audio?.release() }
        if (!closingByUser && isFinishing) sendControl(StreamActions.STOP)
        super.onDestroy()
    }

    @Deprecated("Still called below Android 13, where the back callback does not exist.")
    override fun onBackPressed() = handleBack()

    private fun keyboardShowing() = window.decorView.rootWindowInsets?.isVisible(WindowInsets.Type.ime()) == true

    private fun handleBack() {
        if (keyboardShowing()) {
            hideKeyboard()
            return
        }
        close(StreamActions.STOP)
    }

    // ---- Layout ----

    private fun buildLayout(): View {
        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        stage = FrameLayout(this)
        root.addView(stage, FrameLayout.LayoutParams(MATCH, MATCH))
        surface = SurfaceView(this)
        stage.addView(surface, FrameLayout.LayoutParams(MATCH, MATCH, Gravity.CENTER))
        stage.addOnLayoutChangeListener { _, l, t, r, b, _, _, _, _ -> fitSurface(r - l, b - t) }
        surface.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                decoderSurface = holder.surface
                // Replaying buffered video starts the decoder: keep that off the UI thread.
                Thread({ MediaChannel.attachVideo(MediaSink { header, payload -> onVideo(header, payload) }) }, "Baton viewer attach").start()
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
            override fun surfaceDestroyed(holder: SurfaceHolder) {
                MediaChannel.attachVideo(null)
                releaseDecoder()
                decoderSurface = null
            }
        })
        surface.setOnTouchListener { _, event -> onTouch(event); true }

        placeholder = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(Color.rgb(18, 19, 24))
            val art = activityInfo?.artworkJpegBase64?.let { runCatching { Base64.decode(it, Base64.DEFAULT) }.getOrNull() }
                ?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
            if (art != null) {
                addView(ImageView(context).apply {
                    setImageBitmap(art)
                    scaleType = ImageView.ScaleType.CENTER_CROP
                    clipToOutline = true
                    background = rounded(Color.DKGRAY, 16f)
                }, LinearLayout.LayoutParams(dp(120), dp(120)))
            }
            addView(TextView(context).apply {
                text = activityInfo?.title ?: "Connecting to your PC"
                setTextColor(Color.WHITE)
                textSize = 18f
                typeface = Typeface.DEFAULT_BOLD
                gravity = Gravity.CENTER
                setPadding(dp(24), dp(16), dp(24), dp(8))
            })
            addView(ProgressBar(context), LinearLayout.LayoutParams(dp(32), dp(32)))
        }
        root.addView(placeholder, FrameLayout.LayoutParams(MATCH, MATCH))

        keyboard = KeyCaptureView(this)
        root.addView(keyboard, FrameLayout.LayoutParams(1, 1))

        toolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(12), dp(8), dp(8), dp(8))
            background = rounded(Color.argb(200, 28, 28, 34), 28f)
            addView(TextView(context).apply {
                text = activityInfo?.title ?: ""
                setTextColor(Color.WHITE)
                textSize = 14f
                maxLines = 1
                ellipsize = android.text.TextUtils.TruncateAt.END
                setPadding(dp(4), 0, dp(8), 0)
            }, LinearLayout.LayoutParams(0, WRAP, 1f))
            modeButton = pill("Touch") { toggleMode() }
            addView(modeButton)
            addView(pill("⌨") { toggleKeyboard() })
            fitButton = pill("Fit") { toggleFit() }
            addView(fitButton)
            addView(pill("⟲") { rotate() })
            addView(pill("On PC") { close(StreamActions.RETURN) })
            addView(pill("✕") { close(StreamActions.STOP) })
        }
        root.addView(toolbar, FrameLayout.LayoutParams(MATCH, WRAP, Gravity.TOP).apply { setMargins(dp(8), dp(8), dp(8), 0) })

        handle = TextView(this).apply {
            text = "•••"
            setTextColor(Color.WHITE)
            textSize = 14f
            gravity = Gravity.CENTER
            background = rounded(Color.argb(140, 0, 0, 0), 20f)
            setOnClickListener { setToolbarVisible(true) }
        }
        root.addView(handle, FrameLayout.LayoutParams(dp(56), dp(28), Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = dp(4) })

        mediaBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            setPadding(dp(8), dp(4), dp(8), dp(4))
            background = rounded(Color.argb(200, 28, 28, 34), 28f)
            addView(pill("−10") { media(MediaActions.SKIP, -10_000) })
            addView(pill("⏯") { media(MediaActions.TOGGLE) })
            addView(pill("+10") { media(MediaActions.SKIP, 10_000) })
            visibility = if (activityInfo?.playback != null) View.VISIBLE else View.GONE
        }
        root.addView(mediaBar, FrameLayout.LayoutParams(WRAP, WRAP, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = dp(12) })

        functionRow = keyRowOf((1..12).map { "F$it" to KeyEvent.KEYCODE_F1 + it - 1 } + listOf(
            "Home" to KeyEvent.KEYCODE_MOVE_HOME, "End" to KeyEvent.KEYCODE_MOVE_END,
            "PgUp" to KeyEvent.KEYCODE_PAGE_UP, "PgDn" to KeyEvent.KEYCODE_PAGE_DOWN, "Ins" to KeyEvent.KEYCODE_INSERT
        )).apply { visibility = View.GONE }
        keyRow = keyRowOf(listOf(
            "Esc" to KeyEvent.KEYCODE_ESCAPE, "Tab" to KeyEvent.KEYCODE_TAB,
            "Ctrl" to -KeyEvent.META_CTRL_ON, "Alt" to -KeyEvent.META_ALT_ON, "Shift" to -KeyEvent.META_SHIFT_ON, "Win" to -KeyEvent.META_META_ON,
            "←" to KeyEvent.KEYCODE_DPAD_LEFT, "↑" to KeyEvent.KEYCODE_DPAD_UP, "↓" to KeyEvent.KEYCODE_DPAD_DOWN, "→" to KeyEvent.KEYCODE_DPAD_RIGHT,
            "Del" to KeyEvent.KEYCODE_FORWARD_DEL, "Fn" to 0
        )).apply { visibility = View.GONE }
        val keys = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(wrapScroll(functionRow))
            addView(wrapScroll(keyRow))
        }
        root.addView(keys, FrameLayout.LayoutParams(MATCH, WRAP, Gravity.BOTTOM))
        root.setOnApplyWindowInsetsListener { _, insets ->
            // Keep the key rows right above the on-screen keyboard.
            val ime = insets.getInsets(WindowInsets.Type.ime()).bottom
            (keys.layoutParams as FrameLayout.LayoutParams).bottomMargin = ime
            keys.requestLayout()
            val showing = ime > 0
            keyRow.visibility = if (showing) View.VISIBLE else View.GONE
            if (!showing) functionRow.visibility = View.GONE
            imeHeight = ime
            panForKeyboard()
            insets
        }
        return root
    }

    private fun keyRowOf(keys: List<Pair<String, Int>>) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        setPadding(dp(4), dp(4), dp(4), dp(4))
        setBackgroundColor(Color.argb(230, 32, 33, 40))
        keys.forEach { (label, code) ->
            val view = pill(label) {}
            view.setOnClickListener {
                when {
                    label == "Fn" -> functionRow.visibility = if (functionRow.visibility == View.VISIBLE) View.GONE else View.VISIBLE
                    code < 0 -> {
                        val meta = -code
                        if (!modifiers.remove(meta)) modifiers += meta
                        view.alpha = if (meta in modifiers) 1f else 0.75f
                        view.background = rounded(if (meta in modifiers) Color.rgb(79, 70, 229) else Color.argb(90, 255, 255, 255), 18f)
                    }
                    else -> sendKey(code)
                }
            }
            addView(view)
        }
    }

    private fun wrapScroll(row: View) = HorizontalScrollView(this).apply {
        isHorizontalScrollBarEnabled = false
        addView(row)
        row.visibilityChanges { visibility = it }
    }

    private fun View.visibilityChanges(onChange: (Int) -> Unit) {
        viewTreeObserver.addOnGlobalLayoutListener { onChange(visibility) }
    }

    private fun pill(label: String, onClick: () -> Unit) = TextView(this).apply {
        text = label
        setTextColor(Color.WHITE)
        textSize = 14f
        gravity = Gravity.CENTER
        minWidth = dp(44)
        setPadding(dp(12), dp(8), dp(12), dp(8))
        background = rounded(Color.argb(90, 255, 255, 255), 18f)
        layoutParams = LinearLayout.LayoutParams(WRAP, dp(40)).apply { setMargins(dp(3), 0, dp(3), 0) }
        setOnClickListener {
            onClick()
            main.removeCallbacks(hideToolbar)
            main.postDelayed(hideToolbar, TOOLBAR_MS)
        }
    }

    private fun rounded(color: Int, radius: Float) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = radius * resources.displayMetrics.density
    }

    private fun setToolbarVisible(visible: Boolean) {
        main.removeCallbacks(hideToolbar)
        toolbar.visibility = if (visible) View.VISIBLE else View.GONE
        handle.visibility = if (visible) View.GONE else View.VISIBLE
        if (activityInfo?.playback != null) mediaBar.visibility = if (visible) View.VISIBLE else View.GONE
        if (visible) main.postDelayed(hideToolbar, TOOLBAR_MS)
    }

    /** Sizes the video to the screen keeping the stream's shape. */
    private fun fitSurface(width: Int, height: Int) {
        if (width == 0 || height == 0) return
        val scale = min(width.toFloat() / frameWidth, height.toFloat() / frameHeight)
        val params = surface.layoutParams as FrameLayout.LayoutParams
        val targetWidth = (frameWidth * scale).toInt()
        val targetHeight = (frameHeight * scale).toInt()
        if (params.width != targetWidth || params.height != targetHeight) {
            params.width = targetWidth
            params.height = targetHeight
            surface.post { surface.layoutParams = params }
        }
    }

    private fun applyOrientation() {
        requestedOrientation = if (frameWidth >= frameHeight) {
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
        }
        fitSurface(stage.width, stage.height)
    }

    private fun hideSystemBars() {
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    // ---- Stream ----

    private fun onMeta(json: String) {
        val meta = runCatching { Json.parseToJsonElement(json).jsonObject }.getOrNull() ?: return
        if (meta["session"]?.jsonPrimitive?.content != sessionId && sessionId.isNotEmpty()) return
        when (meta["type"]?.jsonPrimitive?.content) {
            "format" -> {
                val width = meta["width"]?.jsonPrimitive?.int ?: return
                val height = meta["height"]?.jsonPrimitive?.int ?: return
                if (width != frameWidth || height != frameHeight) {
                    frameWidth = width
                    frameHeight = height
                    releaseDecoder()
                    applyOrientation()
                }
            }
            "end" -> if (!closingByUser) {
                Toast.makeText(this, "Stream ended: ${meta["reason"]?.jsonPrimitive?.content ?: ""}", Toast.LENGTH_SHORT).show()
                closingByUser = true
                finish()
            }
        }
    }

    /** Runs on the media channel's reader thread. */
    private fun onVideo(header: StreamProtocol.RecordHeader, payload: ByteArray) {
        if (closing) return
        if (header.isCodecConfig) {
            startDecoder()
        }
        val codec = decoder ?: return
        if (!decoderReady) return
        try {
            val index = codec.dequeueInputBuffer(30_000)
            if (index < 0) return
            val buffer = codec.getInputBuffer(index)!!
            buffer.clear()
            buffer.put(payload)
            val flags = when {
                header.isCodecConfig -> MediaCodec.BUFFER_FLAG_CODEC_CONFIG
                header.isKeyframe -> MediaCodec.BUFFER_FLAG_KEY_FRAME
                else -> 0
            }
            codec.queueInputBuffer(index, 0, payload.size, header.presentationTimeUs, flags)
        } catch (e: IllegalStateException) {
            Log.w(TAG, "Decoder rejected a frame: ${e.message}")
        }
    }

    @Synchronized
    private fun startDecoder() {
        if (decoder != null) return
        val target = decoderSurface ?: return
        try {
            val codec = spareDecoder?.also { spareDecoder = null } ?: MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, frameWidth, frameHeight)
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            format.setInteger(MediaFormat.KEY_PRIORITY, 0)
            codec.configure(format, target, null, 0)
            codec.start()
            decoder = codec
            decoderReady = true
            // Frames are queued in order on the channel's thread; this one shows each as soon as it is decoded.
            Thread({ drain(codec) }, "Baton decoder output").start()
        } catch (e: Exception) {
            Log.w(TAG, "Decoder start failed: ${e.message}")
            releaseDecoder()
        }
    }

    private fun drain(codec: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (decoder === codec) {
            val index = try {
                codec.dequeueOutputBuffer(info, 20_000)
            } catch (e: IllegalStateException) {
                break
            }
            if (index < 0) continue
            runCatching { codec.releaseOutputBuffer(index, true) }
            if (!firstFrameShown && info.size > 0) main.post { onFirstFrame() }
        }
    }

    @Synchronized
    private fun releaseDecoder() {
        decoderReady = false
        val codec = decoder ?: return
        decoder = null
        runCatching { codec.stop() }
        runCatching { codec.release() }
    }

    private fun onFirstFrame() {
        if (firstFrameShown) return
        firstFrameShown = true
        placeholder.animate().alpha(0f).setDuration(150).withEndAction {
            placeholder.visibility = View.GONE
            placeholder.alpha = 1f
        }
        val elapsed = SystemClock.elapsedRealtime() - startedAt
        Log.i(TAG, "First frame after $elapsed ms")
        sendControl(StreamActions.FIRST_FRAME, elapsed)
    }

    private fun requestKeyframe() = MediaChannel.send(StreamProtocol.CHANNEL_CONTROL, ControlMessageCodec.keyframeRequest())

    private fun playAudio(pcm: ByteArray) {
        val track = audio ?: AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MOVIE).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(48_000).setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(AudioTrack.getMinBufferSize(48_000, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT) * 3)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build().also {
                it.play()
                audio = it
            }
        // Never block the channel's reader: if the track is full, this bit of sound is late anyway.
        track.write(pcm, 0, pcm.size, AudioTrack.WRITE_NON_BLOCKING)
    }

    // ---- Input ----

    private fun toFrame(x: Float, y: Float): Pair<Int, Int> =
        (x / surface.width * frameWidth).roundToInt().coerceIn(0, frameWidth - 1) to
            (y / surface.height * frameHeight).roundToInt().coerceIn(0, frameHeight - 1)

    private fun control(bytes: ByteArray) = MediaChannel.send(StreamProtocol.CHANNEL_CONTROL, bytes)

    private var imeHeight = 0
    private var lastTouchY = 0f

    /**
     * The keyboard covers most of a landscape screen: slide the picture up so the place last
     * touched (usually the text field being typed into) sits just above the key row.
     */
    private fun panForKeyboard() {
        val target = if (imeHeight == 0) 0f else {
            val visibleBottom = root.height - imeHeight - dp(56) - dp(24)
            min(0f, visibleBottom - (surface.top + lastTouchY))
        }
        stage.animate().translationY(target).setDuration(150).start()
    }

    private fun onTouch(event: MotionEvent) {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) lastTouchY = event.y
        if (trackpad) onTrackpad(event) else onDirectTouch(event)
    }

    /** Every finger goes to the PC as real touch; the PC's apps do the rest. */
    private fun onDirectTouch(event: MotionEvent) {
        fun send(action: Int, index: Int) {
            val (x, y) = toFrame(event.getX(index), event.getY(index))
            control(ControlMessageCodec.touch(action, event.getPointerId(index).toLong(), x, y, frameWidth, frameHeight))
        }
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> send(ControlMessageCodec.TOUCH_DOWN, event.actionIndex)
            MotionEvent.ACTION_MOVE -> for (index in 0 until event.pointerCount) send(ControlMessageCodec.TOUCH_MOVE, index)
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> send(ControlMessageCodec.TOUCH_UP, event.actionIndex)
            MotionEvent.ACTION_CANCEL -> for (index in 0 until event.pointerCount) send(ControlMessageCodec.TOUCH_CANCEL, index)
        }
    }

    private var padDownAt = 0L
    private var padLastX = 0f
    private var padLastY = 0f
    private var padMoved = false
    private var padFingers = 0
    private var padDragging = false
    private var padLastTapAt = 0L
    private var padScrollY = 0f
    private var padPinchDistance = 0f
    private var zoom = 1f

    /**
     * The phone as a laptop trackpad: slide to move the cursor, tap to click, tap then hold and
     * slide to drag, two-finger tap to right-click, two-finger slide to scroll, pinch to zoom the
     * picture.
     */
    private fun onTrackpad(event: MotionEvent) {
        val scale = frameWidth.toFloat() / max(1, surface.width) / zoom
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                padDownAt = SystemClock.uptimeMillis()
                padLastX = event.rawX
                padLastY = event.rawY
                padMoved = false
                padFingers = 1
                if (padDownAt - padLastTapAt < DOUBLE_TAP_MS) {
                    padDragging = true
                    control(ControlMessageCodec.mouseButton(ControlMessageCodec.BUTTON_LEFT, ControlMessageCodec.KEY_DOWN))
                }
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                padFingers = max(padFingers, event.pointerCount)
                padScrollY = averageY(event)
                padPinchDistance = spread(event)
            }
            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount >= 2) {
                    val distance = spread(event)
                    if (padPinchDistance > 0 && abs(distance - padPinchDistance) > dp(24)) {
                        setZoom(zoom * distance / padPinchDistance, event)
                        padPinchDistance = distance
                        padMoved = true
                        return
                    }
                    val y = averageY(event)
                    val notches = ((y - padScrollY) / dp(28)).toInt()
                    if (notches != 0) {
                        control(ControlMessageCodec.scroll(0, 0, 0, 0, 0, notches))
                        padScrollY = y
                        padMoved = true
                    }
                    return
                }
                val dx = event.rawX - padLastX
                val dy = event.rawY - padLastY
                if (!padMoved && hypot(dx, dy) < dp(6)) return
                padMoved = true
                // A little acceleration, like a real trackpad: fast flicks go further.
                val speed = 1f + min(1.5f, hypot(dx, dy) / dp(40))
                control(ControlMessageCodec.mouseMove((dx * scale * speed).roundToInt(), (dy * scale * speed).roundToInt()))
                padLastX = event.rawX
                padLastY = event.rawY
            }
            MotionEvent.ACTION_UP -> {
                val quick = SystemClock.uptimeMillis() - padDownAt < TAP_MS
                when {
                    padDragging -> {
                        padDragging = false
                        control(ControlMessageCodec.mouseButton(ControlMessageCodec.BUTTON_LEFT, ControlMessageCodec.KEY_UP))
                        if (!padMoved) click(ControlMessageCodec.BUTTON_LEFT)
                        padLastTapAt = 0
                    }
                    padFingers >= 2 && quick && !padMoved -> click(ControlMessageCodec.BUTTON_RIGHT)
                    padFingers == 1 && quick && !padMoved -> {
                        click(ControlMessageCodec.BUTTON_LEFT)
                        padLastTapAt = SystemClock.uptimeMillis()
                    }
                }
            }
        }
    }

    private fun click(button: Int) {
        control(ControlMessageCodec.mouseButton(button, ControlMessageCodec.KEY_DOWN))
        control(ControlMessageCodec.mouseButton(button, ControlMessageCodec.KEY_UP))
    }

    private fun averageY(event: MotionEvent) = (0 until event.pointerCount).map { event.getY(it) }.average().toFloat()

    private fun spread(event: MotionEvent) = if (event.pointerCount < 2) 0f else hypot(event.getX(0) - event.getX(1), event.getY(0) - event.getY(1))

    private fun setZoom(value: Float, event: MotionEvent) {
        zoom = value.coerceIn(1f, 4f)
        stage.pivotX = (event.getX(0) + event.getX(1)) / 2 + surface.left
        stage.pivotY = (event.getY(0) + event.getY(1)) / 2 + surface.top
        stage.scaleX = zoom
        stage.scaleY = zoom
    }

    private fun toggleMode() {
        trackpad = !trackpad
        modeButton.text = if (trackpad) "Trackpad" else "Touch"
        if (!trackpad) {
            zoom = 1f
            stage.scaleX = 1f
            stage.scaleY = 1f
        }
        Toast.makeText(this, if (trackpad) "Trackpad: slide to move, tap to click, two fingers to scroll or zoom" else "Touch: use the PC app with your fingers", Toast.LENGTH_SHORT).show()
    }

    private fun toggleFit() {
        fitted = !fitted
        fitButton.alpha = if (fitted) 1f else 0.6f
        sendControl(if (fitted) StreamActions.FIT else StreamActions.UNFIT)
    }

    private fun rotate() {
        control(ControlMessageCodec.rotate(if (frameWidth >= frameHeight) 0 else 1))
    }

    private fun toggleKeyboard() {
        val keyboardManager = getSystemService(InputMethodManager::class.java)
        if (keyboardShowing()) {
            hideKeyboard()
        } else {
            // Focusable only while typing, so the keyboard never pops up on its own.
            keyboard.isFocusable = true
            keyboard.isFocusableInTouchMode = true
            keyboard.requestFocus()
            keyboardManager.showSoftInput(keyboard, InputMethodManager.SHOW_IMPLICIT)
        }
    }

    private fun hideKeyboard() {
        getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(keyboard.windowToken, 0)
        keyboard.clearFocus()
        keyboard.isFocusable = false
    }

    private fun metaState(): Int = modifiers.fold(0) { state, meta -> state or meta }

    private fun releaseModifiers() {
        if (modifiers.isEmpty()) return
        modifiers.clear()
        for (index in 0 until keyRow.childCount) {
            (keyRow.getChildAt(index) as? TextView)?.let {
                it.alpha = 1f
                it.background = rounded(Color.argb(90, 255, 255, 255), 18f)
            }
        }
    }

    /** A physical (or Bluetooth) keyboard: keys go straight to the PC, shortcuts included. */
    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_VOLUME_UP || keyCode == KeyEvent.KEYCODE_VOLUME_DOWN) {
            return super.onKeyDown(keyCode, event)
        }
        if (KeyEvent.isModifierKey(keyCode)) return true
        val meta = event.metaState and PC_META
        val shortcut = meta and (KeyEvent.META_CTRL_ON or KeyEvent.META_ALT_ON or KeyEvent.META_META_ON)
        val unicode = event.unicodeChar
        if (shortcut == 0 && unicode != 0 && keyCode != KeyEvent.KEYCODE_ENTER && keyCode != KeyEvent.KEYCODE_TAB) {
            control(ControlMessageCodec.text(unicode.toChar().toString()))
        } else {
            control(ControlMessageCodec.key(ControlMessageCodec.KEY_DOWN, keyCode, event.repeatCount, meta))
            control(ControlMessageCodec.key(ControlMessageCodec.KEY_UP, keyCode, 0, meta))
        }
        return true
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean =
        if (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_VOLUME_UP || keyCode == KeyEvent.KEYCODE_VOLUME_DOWN) super.onKeyUp(keyCode, event) else true

    private fun sendKey(keyCode: Int) {
        control(ControlMessageCodec.key(ControlMessageCodec.KEY_DOWN, keyCode, 0, metaState()))
        control(ControlMessageCodec.key(ControlMessageCodec.KEY_UP, keyCode, 0, metaState()))
        releaseModifiers()
    }

    /** Typed text; with Ctrl/Alt/Win held it becomes shortcuts (Ctrl+C...) instead of characters. */
    private fun sendTyped(text: String) {
        if (modifiers.isEmpty() || modifiers == setOf(KeyEvent.META_SHIFT_ON)) {
            control(ControlMessageCodec.text(text))
            releaseModifiers()
            return
        }
        val events = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD).getEvents(text.lowercase().toCharArray())
        val codes = events?.filter { it.action == KeyEvent.ACTION_DOWN && !KeyEvent.isModifierKey(it.keyCode) }?.map { it.keyCode }.orEmpty()
        val meta = metaState()
        codes.forEach {
            control(ControlMessageCodec.key(ControlMessageCodec.KEY_DOWN, it, 0, meta))
            control(ControlMessageCodec.key(ControlMessageCodec.KEY_UP, it, 0, meta))
        }
        releaseModifiers()
    }

    private fun media(action: String, offset: Long? = null) {
        val activity = activityInfo ?: return
        Link.send(MessageTypes.MEDIA_COMMAND, MediaCommandPayload(ownerDeviceId, activity.id, action, offset))
    }

    private fun sendControl(action: String, elapsed: Long? = null) {
        if (sessionId.isNotEmpty()) Link.send(MessageTypes.STREAM_CONTROL, StreamControlPayload(sessionId, action, elapsed))
    }

    private fun close(action: String) {
        closingByUser = true
        sendControl(action)
        finish()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    /**
     * An invisible view the keyboard types into. It keeps no text: every character, deletion and
     * key goes to the PC as it happens, so autocorrect and suggestions cannot rewrite what was sent.
     */
    private inner class KeyCaptureView(context: Context) : View(context) {
        init {
            isFocusable = false
        }

        override fun onCheckIsTextEditor() = true

        override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
            outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_ACTION_NONE
            return object : BaseInputConnection(this, false) {
                private var composing = ""

                override fun commitText(text: CharSequence, newCursorPosition: Int): Boolean {
                    replaceComposing(text.toString())
                    composing = ""
                    return true
                }

                override fun setComposingText(text: CharSequence, newCursorPosition: Int): Boolean {
                    replaceComposing(text.toString())
                    composing = text.toString()
                    return true
                }

                override fun finishComposingText(): Boolean {
                    composing = ""
                    return true
                }

                /** Types only the difference, so a keyboard that composes still sends each letter once. */
                private fun replaceComposing(next: String) {
                    val common = composing.commonPrefixWith(next).length
                    repeat(composing.length - common) { sendKey(KeyEvent.KEYCODE_DEL) }
                    val added = next.substring(common)
                    if (added == "\n") sendKey(KeyEvent.KEYCODE_ENTER) else if (added.isNotEmpty()) sendTyped(added)
                }

                override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                    repeat(beforeLength.coerceAtMost(64)) { sendKey(KeyEvent.KEYCODE_DEL) }
                    repeat(afterLength.coerceAtMost(64)) { sendKey(KeyEvent.KEYCODE_FORWARD_DEL) }
                    return true
                }

                override fun sendKeyEvent(event: KeyEvent): Boolean {
                    if (event.action == KeyEvent.ACTION_DOWN && !KeyEvent.isModifierKey(event.keyCode)) {
                        val unicode = event.unicodeChar
                        if (unicode != 0 && modifiers.isEmpty() && event.keyCode != KeyEvent.KEYCODE_ENTER && event.keyCode != KeyEvent.KEYCODE_TAB) {
                            sendTyped(unicode.toChar().toString())
                        } else {
                            sendKey(event.keyCode)
                        }
                    }
                    return true
                }

                override fun performEditorAction(actionCode: Int): Boolean {
                    sendKey(KeyEvent.KEYCODE_ENTER)
                    return true
                }
            }
        }
    }

    companion object {
        private const val TAG = "BatonViewer"
        private const val MATCH = ViewGroup.LayoutParams.MATCH_PARENT
        private const val WRAP = ViewGroup.LayoutParams.WRAP_CONTENT
        private const val TOOLBAR_MS = 4_000L
        private const val PC_META = KeyEvent.META_SHIFT_ON or KeyEvent.META_ALT_ON or KeyEvent.META_CTRL_ON or KeyEvent.META_META_ON
        private const val TAP_MS = 220L
        private const val DOUBLE_TAP_MS = 280L
        private const val EXTRA_SESSION = "session"
        private const val EXTRA_OWNER = "owner"
        private const val EXTRA_ACTIVITY = "activity"
        private const val EXTRA_STARTED = "started"
        private const val EXTRA_WIDTH = "width"
        private const val EXTRA_HEIGHT = "height"

        private var current: WeakReference<StreamViewerActivity>? = null

        /** The session the open viewer is showing, if any. */
        val openSession: String? get() = current?.get()?.sessionId

        fun intent(
            context: Context,
            sessionId: String,
            ownerDeviceId: String,
            activity: BatonActivity?,
            startedAt: Long = SystemClock.elapsedRealtime(),
            width: Int = 0,
            height: Int = 0
        ): Intent = Intent(context, StreamViewerActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(EXTRA_SESSION, sessionId)
            .putExtra(EXTRA_OWNER, ownerDeviceId)
            .putExtra(EXTRA_STARTED, startedAt)
            .putExtra(EXTRA_WIDTH, width)
            .putExtra(EXTRA_HEIGHT, height)
            .apply { activity?.let { putExtra(EXTRA_ACTIVITY, Wire.json.encodeToString(BatonActivity.serializer(), it)) } }
    }
}
