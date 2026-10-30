package dev.baton.android.stream

import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The live sound of a stream or mirror as Opus: 48 kHz stereo, 160 kbit/s, a tenth of the PCM it
 * replaces and just as clear. Twin of the PC's `Baton.Media.OpusAudio`; each side only sends Opus
 * once the other has said it plays it, so older versions keep getting PCM. Used away from home
 * only: on the home Wi-Fi each packet's MediaCodec calls cost more battery than the bandwidth saves.
 */
object OpusAudio {
    const val CODEC = "opus"
    const val SAMPLE_RATE = 48_000
    const val CHANNELS = 2
    const val BITRATE = 160_000

    val canDecode: Boolean by lazy { supported(encoder = false) }
    val canEncode: Boolean by lazy { supported(encoder = true) }

    /** The compressed audio this phone plays, for its device info. */
    val decodable: List<String>? get() = if (canDecode) listOf(CODEC) else null

    internal fun format(): MediaFormat = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, SAMPLE_RATE, CHANNELS)

    private fun supported(encoder: Boolean): Boolean = runCatching {
        val codecs = MediaCodecList(MediaCodecList.REGULAR_CODECS)
        if (encoder) codecs.findEncoderForFormat(format().apply { setInteger(MediaFormat.KEY_BIT_RATE, BITRATE) }) != null
        else codecs.findDecoderForFormat(format()) != null
    }.getOrDefault(false)

    /** RFC 7845 identification header: stereo at 48 kHz, no pre-skip (live sound has no start to trim). */
    fun opusHead(): ByteArray = ByteBuffer.allocate(19).order(ByteOrder.LITTLE_ENDIAN).apply {
        put("OpusHead".toByteArray(Charsets.US_ASCII))
        put(1)
        put(CHANNELS.toByte())
        putShort(0)
        putInt(SAMPLE_RATE)
        putShort(0)
        put(0)
    }.array()

    internal fun nanos(value: Long): ByteBuffer = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).apply { flip() }
}

/**
 * Feeds [MediaCodec] and collects what it has ready, all on the caller's thread: audio arrives in
 * small, steady pieces, so nothing waits for long.
 */
abstract class OpusCodec(protected val codec: MediaCodec) : AutoCloseable {
    private val info = MediaCodec.BufferInfo()
    private var scratch = ByteArray(8 * 1024)

    protected fun process(data: ByteArray, length: Int, presentationTimeUs: Long, onOutput: (ByteArray, Int, Long) -> Unit) {
        var offset = 0
        while (offset < length) {
            val index = codec.dequeueInputBuffer(INPUT_WAIT_US)
            if (index < 0) break
            val buffer = codec.getInputBuffer(index) ?: break
            buffer.clear()
            val take = minOf(buffer.remaining(), length - offset)
            buffer.put(data, offset, take)
            codec.queueInputBuffer(index, 0, take, presentationTimeUs, 0)
            offset += take
        }
        while (true) {
            val index = codec.dequeueOutputBuffer(info, 0)
            if (index == MediaCodec.INFO_TRY_AGAIN_LATER) break
            if (index < 0) continue
            // The codec's own header comes out first; the other side already knows the format.
            if (info.size > 0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) {
                val buffer = codec.getOutputBuffer(index)
                if (buffer != null) {
                    if (scratch.size < info.size) scratch = ByteArray(info.size)
                    buffer.position(info.offset)
                    buffer.get(scratch, 0, info.size)
                    onOutput(scratch, info.size, info.presentationTimeUs)
                }
            }
            codec.releaseOutputBuffer(index, false)
        }
    }

    override fun close() {
        runCatching { codec.stop() }
        runCatching { codec.release() }
    }

    private companion object {
        const val INPUT_WAIT_US = 10_000L
    }
}

/** Opus packets from the PC in, PCM for the AudioTrack out. */
class OpusDecoder : OpusCodec(MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS)) {
    private var nextUs = 0L

    init {
        val format = OpusAudio.format().apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(OpusAudio.opusHead()))
            setByteBuffer("csd-1", OpusAudio.nanos(0))
            setByteBuffer("csd-2", OpusAudio.nanos(SEEK_PREROLL_NS))
        }
        codec.configure(format, null, null, 0)
        codec.start()
    }

    /** [onPcm] gets 16-bit stereo PCM; its array is reused, so it must be consumed at once. */
    fun decode(packet: ByteArray, onPcm: (ByteArray, Int) -> Unit) {
        // Timestamps of its own: the PC stamps a capture chunk, which can hold several packets.
        process(packet, packet.size, nextUs) { pcm, size, _ -> onPcm(pcm, size) }
        nextUs += PACKET_US
    }

    private companion object {
        const val SEEK_PREROLL_NS = 80_000_000L
        const val PACKET_US = 20_000L
    }
}

/** PCM captured on this phone in, Opus packets for the PC out. */
class OpusEncoder : OpusCodec(MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS)) {
    init {
        codec.configure(OpusAudio.format().apply { setInteger(MediaFormat.KEY_BIT_RATE, OpusAudio.BITRATE) }, null, null,
            MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
    }

    /** [onPacket] gets each packet with its time; its array is reused, so it must be sent at once. */
    fun encode(pcm: ByteArray, length: Int, presentationTimeUs: Long, onPacket: (ByteArray, Int, Long) -> Unit) =
        process(pcm, length, presentationTimeUs, onPacket)
}
