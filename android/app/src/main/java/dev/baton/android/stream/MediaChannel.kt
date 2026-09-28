package dev.baton.android.stream

import android.util.Log
import dev.baton.android.protocol.MediaChannelPayload
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread

/** Receives records of one kind from the media channel. Called on the channel's reader thread. */
fun interface MediaSink {
    fun onRecord(header: StreamProtocol.RecordHeader, payload: ByteArray)
}

/**
 * The persistent media socket to the PC, opened once per session so no stream ever waits for a
 * connection: PC window video and sound arrive here, this phone's screen and input leave here.
 *
 * Video that arrives before a viewer is ready is kept from the last keyframe on (with the
 * stream's format and codec config), so a viewer that opens a moment late still starts at once.
 */
object MediaChannel {
    private const val TAG = "BatonMedia"
    private const val MAX_BUFFERED = 90

    private val generation = AtomicLong(0)
    private val writer = Executors.newSingleThreadExecutor { runnable -> Thread(runnable, "Baton media writer") }
    private val bufferLock = Any()
    private val videoBuffer = ArrayList<Pair<StreamProtocol.RecordHeader, ByteArray>>()
    private var formatMeta: Pair<StreamProtocol.RecordHeader, ByteArray>? = null
    private var codecConfig: Pair<StreamProtocol.RecordHeader, ByteArray>? = null

    @Volatile private var socket: StreamSocketClient? = null
    @Volatile private var records: StreamRecordWriter? = null
    @Volatile private var videoSink: MediaSink? = null
    @Volatile var audioSink: MediaSink? = null
    @Volatile var controlSink: MediaSink? = null

    /** Called for meta records (JSON: format, end). */
    @Volatile var metaSink: MediaSink? = null

    val isOpen: Boolean get() = socket != null

    /** Dials the PC's stream port with the ticket from `media.channel`. */
    fun open(address: String, fingerprint: String, payload: MediaChannelPayload, onClosed: () -> Unit) {
        val current = generation.incrementAndGet()
        close(current)
        thread(name = "Baton media reader") {
            try {
                val client = StreamSocketClient.connect(
                    address, payload.port, StreamProtocol.KIND_MEDIA, payload.ticket, payload.streamId,
                    useTls = true, certificateFingerprint = fingerprint
                )
                if (generation.get() != current) {
                    client.close()
                    return@thread
                }
                socket = client
                records = StreamRecordWriter(client.output)
                Log.i(TAG, "Media channel open")
                read(client)
            } catch (e: Exception) {
                Log.w(TAG, "Media channel ended: ${e.message}")
            } finally {
                if (generation.get() == current) {
                    socket = null
                    records = null
                    onClosed()
                }
            }
        }
    }

    fun close() = close(generation.incrementAndGet())

    private fun close(keep: Long) {
        if (generation.get() != keep) return
        runCatching { socket?.close() }
        socket = null
        records = null
    }

    /** Attaches the video viewer and replays what arrived before it. */
    fun attachVideo(sink: MediaSink?) {
        synchronized(bufferLock) {
            videoSink = sink
            if (sink == null) return
            formatMeta?.let { (header, payload) -> metaSink?.onRecord(header, payload) }
            codecConfig?.let { (header, payload) -> sink.onRecord(header, payload) }
            videoBuffer.forEach { (header, payload) -> sink.onRecord(header, payload) }
        }
    }

    /** Queues a record to the PC. False when the channel is not open. */
    fun send(channel: Int, payload: ByteArray, flags: Int = 0, presentationTimeUs: Long = 0): Boolean {
        val target = records ?: return false
        writer.execute {
            runCatching { target.write(payload, flags, presentationTimeUs, channel) }
                .onFailure { Log.w(TAG, "Media send failed: ${it.message}") }
        }
        return true
    }

    /** Sends immediately on the calling thread; for the encoder, which has its own thread. */
    fun sendNow(channel: Int, payload: ByteArray, flags: Int, presentationTimeUs: Long, offset: Int = 0, length: Int = payload.size - offset): Boolean {
        val target = records ?: return false
        return runCatching { target.write(payload, flags, presentationTimeUs, channel, offset, length) }.isSuccess
    }

    fun clearVideo() = synchronized(bufferLock) {
        videoBuffer.clear()
        codecConfig = null
        formatMeta = null
    }

    private fun read(client: StreamSocketClient) {
        val input = client.input
        while (true) {
            val header = StreamProtocol.readHeader(input)
            val payload = ByteArray(header.length)
            StreamProtocol.readFully(input, payload)
            when (header.channel) {
                StreamProtocol.CHANNEL_VIDEO -> synchronized(bufferLock) {
                    when {
                        header.isCodecConfig -> {
                            codecConfig = header to payload
                            videoBuffer.clear()
                        }
                        header.isKeyframe -> {
                            videoBuffer.clear()
                            videoBuffer += header to payload
                        }
                        videoBuffer.size < MAX_BUFFERED -> videoBuffer += header to payload
                    }
                    videoSink?.onRecord(header, payload)
                }
                StreamProtocol.CHANNEL_AUDIO -> audioSink?.onRecord(header, payload)
                // The PC's round-trip probe: straight back, so it can measure the link.
                StreamProtocol.CHANNEL_KEEPALIVE -> if (payload.isNotEmpty()) send(StreamProtocol.CHANNEL_KEEPALIVE, payload)
                // The PC's clipboard applies to whichever session is running; other input goes to it.
                StreamProtocol.CHANNEL_CONTROL -> if (payload.firstOrNull()?.toInt() == ControlMessageCodec.TYPE_CLIPBOARD) {
                    (runCatching { ControlMessageCodec.decode(payload) }.getOrNull() as? ControlMessage.Clipboard)?.let { ClipboardSync.onRemote(it.value) }
                } else {
                    controlSink?.onRecord(header, payload)
                }
                StreamProtocol.CHANNEL_META -> synchronized(bufferLock) {
                    val text = String(payload)
                    if (text.contains("\"format\"")) {
                        if (formatMeta == null || !text.contains(String(formatMeta!!.second).substringAfter("\"session\":\"").substringBefore('"'))) {
                            videoBuffer.clear()
                            codecConfig = null
                        }
                        formatMeta = header to payload
                    }
                    metaSink?.onRecord(header, payload)
                }
                else -> Unit
            }
        }
    }
}
