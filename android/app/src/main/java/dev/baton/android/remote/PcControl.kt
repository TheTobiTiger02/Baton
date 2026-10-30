package dev.baton.android.remote

import android.os.SystemClock
import dev.baton.android.link.Link
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.PcControlPayload
import dev.baton.android.protocol.PcStatePayload
import dev.baton.android.stream.ControlMessageCodec
import dev.baton.android.stream.MediaChannel
import dev.baton.android.stream.StreamProtocol
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The remote's link to the PC: commands over the session (`pc.control`, answered with the PC's
 * sound settings), and touchpad and keyboard input over the media channel, as a streamed window
 * gets it, which the PC applies to the whole desktop when nothing is streamed.
 */
object PcControl {
    private const val VOLUME_EVERY_MS = 80L

    private val stateFlow = MutableStateFlow<PcStatePayload?>(null)
    val state: StateFlow<PcStatePayload?> = stateFlow.asStateFlow()
    private var volumeSentAt = 0L

    fun send(action: String, value: Double? = null) {
        Link.send(MessageTypes.PC_CONTROL, PcControlPayload(action, value))
    }

    /** While the slider moves: often enough to follow the finger, not once per frame. */
    fun sendVolume(level: Double) {
        val now = SystemClock.elapsedRealtime()
        if (now - volumeSentAt < VOLUME_EVERY_MS) return
        volumeSentAt = now
        send(dev.baton.android.protocol.PcControlActions.VOLUME, level)
    }

    fun input(record: ByteArray) {
        MediaChannel.send(StreamProtocol.CHANNEL_CONTROL, record)
    }

    fun key(keyCode: Int) = input(ControlMessageCodec.key(ControlMessageCodec.KEY_DOWN, keyCode, 0, 0))

    fun onState(state: PcStatePayload) {
        stateFlow.value = state
    }
}
