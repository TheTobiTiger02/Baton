package dev.baton.android.stream

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Wire format for the dedicated media stream socket.
 *
 * This is the one format three transports share: TLS over the LAN, TLS over Tailscale, and a plain
 * `adb reverse` tunnel. It therefore assumes nothing about WebSockets, and the PC side
 * (`Baton.Host/Streaming/StreamFraming.cs`) must stay byte-identical. Parity is pinned
 * by tests on both sides.
 *
 * ```
 * Handshake (client -> server), 48 bytes, big endian:
 *   magic    u32  = 0x42544E31 ("BTN1")
 *   kind     u8   1 video | 2 audio | 3 control | 4 mic
 *   version  u8   = 1
 *   reserved u16
 *   ticket   u8[32]
 *   streamId u64
 *
 * Record header, 16 bytes, big endian:
 *   flags    u8   bit0 codec config, bit1 keyframe, bit2 format change
 *   reserved u8
 *   sequence u16  wraps; gaps mean loss
 *   ptsUs    u64
 *   length   u32  payload byte count
 *   payload  u8[length]
 * ```
 *
 * Video payload is Annex-B with start codes, exactly as `MediaCodec` emits it, so Media Foundation
 * can consume it without repackaging.
 */
object StreamProtocol {

    const val MAGIC: Int = 0x42544E31
    const val VERSION: Int = 1

    const val HANDSHAKE_BYTES: Int = 48
    const val HEADER_BYTES: Int = 16
    const val TICKET_BYTES: Int = 32

    /** Refuses anything absurd before allocating for it. 16 MiB is far above any sane frame. */
    const val MAX_PAYLOAD_BYTES: Int = 16 * 1024 * 1024

    const val KIND_VIDEO: Int = 1
    const val KIND_AUDIO: Int = 2
    const val KIND_CONTROL: Int = 3
    const val KIND_MIC: Int = 4

    /** PC-to-phone file transfer: after the handshake, the PC writes the raw file bytes. */
    const val KIND_FILE: Int = 5

    const val FLAG_CODEC_CONFIG: Int = 1 shl 0
    const val FLAG_KEYFRAME: Int = 1 shl 1
    const val FLAG_FORMAT_CHANGE: Int = 1 shl 2

    fun encodeHandshake(kind: Int, ticket: ByteArray, streamId: Long): ByteArray {
        require(ticket.size == TICKET_BYTES) { "Ticket must be $TICKET_BYTES bytes, was ${ticket.size}." }
        require(kind in KIND_VIDEO..KIND_MEDIA) { "Unknown stream kind $kind." }

        return ByteBuffer.allocate(HANDSHAKE_BYTES).order(ByteOrder.BIG_ENDIAN).apply {
            putInt(MAGIC)
            put(kind.toByte())
            put(VERSION.toByte())
            putShort(0)
            put(ticket)
            putLong(streamId)
        }.array()
    }

    fun decodeHandshake(bytes: ByteArray): Handshake {
        require(bytes.size >= HANDSHAKE_BYTES) { "Handshake must be $HANDSHAKE_BYTES bytes." }

        val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        val magic = buffer.int
        require(magic == MAGIC) { "Not a Baton stream (magic 0x${magic.toUInt().toString(16)})." }

        val kind = buffer.get().toInt() and 0xFF
        val version = buffer.get().toInt() and 0xFF
        require(version == VERSION) { "Unsupported stream version $version." }
        buffer.short

        val ticket = ByteArray(TICKET_BYTES)
        buffer.get(ticket)
        return Handshake(kind, version, ticket, buffer.long)
    }

    /** Media channel record channels, carried in the header's second byte. */
    const val CHANNEL_VIDEO = 0
    const val CHANNEL_AUDIO = 1
    const val CHANNEL_CONTROL = 2
    const val CHANNEL_META = 3
    const val CHANNEL_KEEPALIVE = 255

    /** The persistent, multiplexed media channel. */
    const val KIND_MEDIA = 6

    fun encodeHeader(flags: Int, sequence: Int, presentationTimeUs: Long, length: Int, channel: Int = CHANNEL_VIDEO): ByteArray {
        require(length in 0..MAX_PAYLOAD_BYTES) { "Payload length $length is out of range." }

        return ByteBuffer.allocate(HEADER_BYTES).order(ByteOrder.BIG_ENDIAN).apply {
            put(flags.toByte())
            put(channel.toByte())
            putShort(sequence.toShort())
            putLong(presentationTimeUs)
            putInt(length)
        }.array()
    }

    fun decodeHeader(bytes: ByteArray, offset: Int = 0): RecordHeader {
        require(bytes.size - offset >= HEADER_BYTES) { "Header must be $HEADER_BYTES bytes." }

        val buffer = ByteBuffer.wrap(bytes, offset, HEADER_BYTES).order(ByteOrder.BIG_ENDIAN)
        val flags = buffer.get().toInt() and 0xFF
        val channel = buffer.get().toInt() and 0xFF
        val sequence = buffer.short.toInt() and 0xFFFF
        val presentationTimeUs = buffer.long
        val length = buffer.int
        require(length in 0..MAX_PAYLOAD_BYTES) { "Payload length $length is out of range." }

        return RecordHeader(flags, sequence, presentationTimeUs, length, channel)
    }

    /** Reads exactly [count] bytes or throws; a short read here is a truncated stream, not EOF. */
    fun readFully(input: InputStream, destination: ByteArray, offset: Int = 0, count: Int = destination.size - offset) {
        var read = 0
        while (read < count) {
            val chunk = input.read(destination, offset + read, count - read)
            if (chunk < 0) {
                throw EOFException("Stream ended after $read of $count bytes.")
            }
            read += chunk
        }
    }

    fun readHeader(input: InputStream): RecordHeader {
        val header = ByteArray(HEADER_BYTES)
        readFully(input, header)
        return decodeHeader(header)
    }

    data class Handshake(val kind: Int, val version: Int, val ticket: ByteArray, val streamId: Long) {
        override fun equals(other: Any?): Boolean =
            other is Handshake &&
                kind == other.kind &&
                version == other.version &&
                streamId == other.streamId &&
                ticket.contentEquals(other.ticket)

        override fun hashCode(): Int {
            var result = kind
            result = 31 * result + version
            result = 31 * result + streamId.hashCode()
            result = 31 * result + ticket.contentHashCode()
            return result
        }
    }

    data class RecordHeader(
        val flags: Int,
        val sequence: Int,
        val presentationTimeUs: Long,
        val length: Int,
        val channel: Int = CHANNEL_VIDEO
    ) {
        val isCodecConfig: Boolean get() = flags and FLAG_CODEC_CONFIG != 0
        val isKeyframe: Boolean get() = flags and FLAG_KEYFRAME != 0
        val isFormatChange: Boolean get() = flags and FLAG_FORMAT_CHANGE != 0
    }
}

/**
 * Writes records to a stream socket. Thread safe: records from different threads (video encoder,
 * audio capture, input) never interleave, and each channel keeps its own sequence.
 */
class StreamRecordWriter(private val output: OutputStream) {
    private val sequences = IntArray(256)

    @Synchronized
    fun write(payload: ByteArray, flags: Int, presentationTimeUs: Long, channel: Int = StreamProtocol.CHANNEL_VIDEO, offset: Int = 0, length: Int = payload.size - offset) {
        require(length <= StreamProtocol.MAX_PAYLOAD_BYTES) { "Payload length $length is out of range." }
        val sequence = sequences[channel and 0xFF]
        sequences[channel and 0xFF] = (sequence + 1) and 0xFFFF
        output.write(StreamProtocol.encodeHeader(flags, sequence, presentationTimeUs, length, channel))
        output.write(payload, offset, length)
        output.flush()
    }

    fun write(payload: ByteBuffer, flags: Int, presentationTimeUs: Long, channel: Int = StreamProtocol.CHANNEL_VIDEO) {
        val bytes = ByteArray(payload.remaining())
        payload.get(bytes)
        write(bytes, flags, presentationTimeUs, channel)
    }
}
