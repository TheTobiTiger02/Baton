package dev.baton.android.stream

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets

/**
 * Input sent between a viewer and the device being shown, one message per media channel control
 * record. Big-endian; `Baton.Media.ControlMessages` on the PC is the byte-identical twin, pinned by
 * parity tests on both sides. Positions are pixels in the video frame plus the frame size they
 * belong to, so a position computed before a rotation can never land somewhere arbitrary.
 */
object ControlMessageCodec {
    const val TYPE_TOUCH = 0x01
    const val TYPE_SCROLL = 0x02
    const val TYPE_KEY = 0x03
    const val TYPE_TEXT = 0x04
    const val TYPE_NAV = 0x05
    const val TYPE_KEYFRAME = 0x08
    const val TYPE_ROTATE = 0x09
    const val TYPE_MOUSE_MOVE = 0x0B
    const val TYPE_MOUSE_BUTTON = 0x0C

    const val TOUCH_DOWN = 0
    const val TOUCH_UP = 1
    const val TOUCH_MOVE = 2
    const val TOUCH_CANCEL = 3

    const val KEY_DOWN = 0
    const val KEY_UP = 1

    const val NAV_BACK = 0
    const val NAV_HOME = 1
    const val NAV_RECENTS = 2
    const val NAV_POWER = 3
    const val NAV_NOTIFICATIONS = 4

    const val BUTTON_LEFT = 1
    const val BUTTON_RIGHT = 2
    const val BUTTON_MIDDLE = 3

    const val PRESSURE_SCALE = 65_535
    const val MAX_TEXT_BYTES = 64 * 1024

    fun touch(action: Int, pointerId: Long, x: Int, y: Int, frameWidth: Int, frameHeight: Int, pressure: Int = PRESSURE_SCALE): ByteArray =
        buffer(1 + 1 + 8 + 4 + 4 + 2 + 2 + 2).apply {
            put(TYPE_TOUCH.toByte())
            put(action.toByte())
            putLong(pointerId)
            putInt(x)
            putInt(y)
            putShort(frameWidth.toShort())
            putShort(frameHeight.toShort())
            putShort(pressure.toShort())
        }.array()

    /** Wheel notches (positive = up / right); frame size 0 means "wherever the cursor is". */
    fun scroll(x: Int, y: Int, frameWidth: Int, frameHeight: Int, horizontal: Int, vertical: Int): ByteArray =
        buffer(1 + 4 + 4 + 2 + 2 + 2 + 2).apply {
            put(TYPE_SCROLL.toByte())
            putInt(x)
            putInt(y)
            putShort(frameWidth.toShort())
            putShort(frameHeight.toShort())
            putShort(horizontal.toShort())
            putShort(vertical.toShort())
        }.array()

    fun key(action: Int, keyCode: Int, repeatCount: Int, metaState: Int): ByteArray =
        buffer(1 + 1 + 4 + 4 + 4).apply {
            put(TYPE_KEY.toByte())
            put(action.toByte())
            putInt(keyCode)
            putInt(repeatCount)
            putInt(metaState)
        }.array()

    fun text(value: String): ByteArray {
        val utf8 = value.toByteArray(StandardCharsets.UTF_8)
        require(utf8.size <= MAX_TEXT_BYTES) { "Text exceeds $MAX_TEXT_BYTES bytes." }
        return buffer(1 + 4 + utf8.size).apply {
            put(TYPE_TEXT.toByte())
            putInt(utf8.size)
            put(utf8)
        }.array()
    }

    fun navigate(target: Int): ByteArray = byteArrayOf(TYPE_NAV.toByte(), target.toByte())

    fun keyframeRequest(): ByteArray = byteArrayOf(TYPE_KEYFRAME.toByte())

    /** 0 portrait, 1 landscape. */
    fun rotate(orientation: Int): ByteArray = byteArrayOf(TYPE_ROTATE.toByte(), orientation.toByte())

    /** Trackpad movement, in frame pixels. */
    fun mouseMove(dx: Int, dy: Int): ByteArray = buffer(1 + 4 + 4).apply {
        put(TYPE_MOUSE_MOVE.toByte())
        putInt(dx)
        putInt(dy)
    }.array()

    fun mouseButton(button: Int, action: Int): ByteArray =
        byteArrayOf(TYPE_MOUSE_BUTTON.toByte(), button.toByte(), action.toByte())

    fun decode(bytes: ByteArray): ControlMessage {
        require(bytes.isNotEmpty()) { "Empty control message." }
        val body = ByteBuffer.wrap(bytes, 1, bytes.size - 1).order(ByteOrder.BIG_ENDIAN)
        return when (bytes[0].toInt() and 0xFF) {
            TYPE_TOUCH -> ControlMessage.Touch(
                action = body.get().toInt() and 0xFF,
                pointerId = body.long,
                x = body.int,
                y = body.int,
                frameWidth = body.short.toInt() and 0xFFFF,
                frameHeight = body.short.toInt() and 0xFFFF,
                pressure = body.short.toInt() and 0xFFFF
            )
            TYPE_SCROLL -> ControlMessage.Scroll(
                x = body.int,
                y = body.int,
                frameWidth = body.short.toInt() and 0xFFFF,
                frameHeight = body.short.toInt() and 0xFFFF,
                horizontal = body.short.toInt(),
                vertical = body.short.toInt()
            )
            TYPE_KEY -> ControlMessage.Key(body.get().toInt() and 0xFF, body.int, body.int, body.int)
            TYPE_TEXT -> {
                val length = body.int
                require(length in 0..MAX_TEXT_BYTES && length <= body.remaining()) { "Control text length $length is out of range." }
                val utf8 = ByteArray(length)
                body.get(utf8)
                ControlMessage.Text(String(utf8, StandardCharsets.UTF_8))
            }
            TYPE_NAV -> ControlMessage.Navigate(body.get().toInt() and 0xFF)
            TYPE_KEYFRAME -> ControlMessage.KeyframeRequest
            TYPE_ROTATE -> ControlMessage.Rotate(body.get().toInt() and 0xFF)
            TYPE_MOUSE_MOVE -> ControlMessage.MouseMove(body.int, body.int)
            TYPE_MOUSE_BUTTON -> ControlMessage.MouseButton(body.get().toInt() and 0xFF, body.get().toInt() and 0xFF)
            else -> throw IllegalArgumentException("Unknown control message type 0x${(bytes[0].toInt() and 0xFF).toString(16)}.")
        }
    }

    private fun buffer(capacity: Int): ByteBuffer = ByteBuffer.allocate(capacity).order(ByteOrder.BIG_ENDIAN)
}

sealed interface ControlMessage {
    data class Touch(val action: Int, val pointerId: Long, val x: Int, val y: Int, val frameWidth: Int, val frameHeight: Int, val pressure: Int) : ControlMessage
    data class Scroll(val x: Int, val y: Int, val frameWidth: Int, val frameHeight: Int, val horizontal: Int, val vertical: Int) : ControlMessage
    data class Key(val action: Int, val keyCode: Int, val repeatCount: Int, val metaState: Int) : ControlMessage
    data class Text(val value: String) : ControlMessage
    data class Navigate(val target: Int) : ControlMessage
    data object KeyframeRequest : ControlMessage
    data class Rotate(val orientation: Int) : ControlMessage
    data class MouseMove(val dx: Int, val dy: Int) : ControlMessage
    data class MouseButton(val button: Int, val action: Int) : ControlMessage
}
