package dev.baton.android.transfer

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Log
import android.webkit.MimeTypeMap
import android.widget.Toast
import dev.baton.android.handoff.Launcher
import dev.baton.android.link.Link
import dev.baton.android.link.PinnedTls
import dev.baton.android.protocol.FileOfferPayload
import dev.baton.android.protocol.FileResultPayload
import dev.baton.android.protocol.FileUploadPayload
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.Wire
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody

/**
 * Files between this phone and the PC, both ways over the pinned TLS connection.
 *
 * To the PC: what is shared to Baton is copied into its cache first (Android's permission to read
 * a shared file ends with the share screen), offered over the session, and uploaded to the
 * one-time `/upload/{token}` the PC answers with. From the PC: fetched from `/files/{token}`,
 * saved in Download/Baton, and opened.
 */
object FileTransfers {
    private const val TAG = "BatonFiles"
    private const val OUTGOING = "outgoing"

    /** One transfer at a time: the link stays responsive, and a phone's storage isn't hammered. */
    private val io = Executors.newSingleThreadExecutor()
    private val pending = ConcurrentHashMap<String, Outgoing>()

    private data class Outgoing(val file: File, val name: String, val mime: String?)

    /**
     * Copies what was shared into the cache and offers it to the PC. Blocking: call it off the
     * main thread, before the share screen closes. Returns how many files were offered.
     */
    fun send(context: Context, uris: List<Uri>): Int {
        val folder = File(context.cacheDir, OUTGOING).apply { mkdirs() }
        var offered = 0
        for (uri in uris) {
            val name = displayName(context, uri)
            val copy = File(folder, Wire.newId())
            val copied = runCatching {
                context.contentResolver.openInputStream(uri)!!.use { input -> copy.outputStream().use { input.copyTo(it) } }
            }.onFailure { Log.w(TAG, "Could not read a shared file", it) }.isSuccess
            if (!copied) {
                copy.delete()
                continue
            }
            val id = Wire.newId()
            val mime = context.contentResolver.getType(uri)
            pending[id] = Outgoing(copy, name, mime)
            if (Link.send(MessageTypes.FILE_OFFER, FileOfferPayload(id, name, copy.length(), mime))) {
                offered++
                Log.i(TAG, "Offered ${copy.length()} bytes")
            } else {
                Log.w(TAG, "Not connected: the offer wasn't sent")
                pending.remove(id)
                copy.delete()
            }
        }
        return offered
    }

    /** The PC accepted a file this phone offered: upload it. */
    fun onUpload(context: Context, upload: FileUploadPayload) {
        val outgoing = pending.remove(upload.transferId) ?: return Unit.also { Log.w(TAG, "Upload asked for an unknown file") }
        Log.i(TAG, "Uploading ${outgoing.file.length()} bytes")
        val app = context.applicationContext
        io.execute {
            val ok = runCatching {
                val request = Request.Builder()
                    .url("https://${Link.pcAddress}:${Link.FILE_PORT}/upload/${upload.token}")
                    .put(outgoing.file.asRequestBody(outgoing.mime?.toMediaTypeOrNull()))
                    .build()
                client().newCall(request).execute().use { response ->
                    if (!response.isSuccessful) Log.w(TAG, "Upload refused: HTTP ${response.code}")
                    response.isSuccessful
                }
            }.onFailure { Log.w(TAG, "Upload failed", it) }.getOrDefault(false)
            outgoing.file.delete()
            if (!ok) toast(app, "${outgoing.name} didn't reach your PC.")
        }
    }

    /** The PC saved (or couldn't save) a file this phone sent. */
    fun onResult(context: Context, result: FileResultPayload) {
        toast(context.applicationContext, if (result.ok) "Sent ${result.detail ?: "the file"} to your PC" else result.detail ?: "The PC couldn't save the file.")
    }

    /** The PC sent a file: save it in Download/Baton and open it. */
    fun onOffer(context: Context, offer: FileOfferPayload) {
        val token = offer.token ?: return
        val app = context.applicationContext
        io.execute {
            val saved = runCatching { download(app, offer, token) }
                .onFailure { Log.w(TAG, "Download failed", it) }.getOrNull()
            Link.send(MessageTypes.FILE_RESULT, FileResultPayload(offer.transferId, saved != null))
            if (saved == null) {
                toast(app, "${offer.name} from your PC didn't arrive.")
                return@execute
            }
            val (uri, mime) = saved
            val open = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            Launcher.start(app, open, offer.name)
        }
    }

    private fun download(context: Context, offer: FileOfferPayload, token: String): Pair<Uri, String> {
        val mime = mimeFor(offer.name, offer.mime)
        val resolver = context.contentResolver
        val request = Request.Builder().url("https://${Link.pcAddress}:${Link.FILE_PORT}/files/$token").build()
        client().newCall(request).execute().use { response ->
            check(response.isSuccessful) { "HTTP ${response.code}" }
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, offer.name)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.RELATIVE_PATH, "${Environment.DIRECTORY_DOWNLOADS}/Baton")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: error("No room in Downloads")
            try {
                resolver.openOutputStream(uri)!!.use { output -> response.body!!.byteStream().copyTo(output) }
                resolver.update(uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
            } catch (e: Exception) {
                resolver.delete(uri, null, null)
                throw e
            }
            return uri to mime
        }
    }

    /** The name a file carries, from its provider; the last path part otherwise. */
    private fun displayName(context: Context, uri: Uri): String =
        runCatching {
            context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }
        }.getOrNull()?.takeIf { it.isNotBlank() } ?: uri.lastPathSegment?.substringAfterLast('/') ?: "file"

    /** What Android calls the file's type: from its name, since the PC only knows media types. */
    fun mimeFor(name: String, offered: String?): String =
        MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase())
            ?: offered?.takeIf { it.isNotBlank() } ?: "application/octet-stream"

    /** Big files: no time limit on the transfer itself, only on connecting and on each stall. */
    private fun client() = PinnedTls.newClient(Link.pcFingerprint).newBuilder()
        .readTimeout(60, TimeUnit.SECONDS)
        .writeTimeout(60, TimeUnit.SECONDS)
        .build()

    private fun toast(context: Context, text: String) {
        android.os.Handler(android.os.Looper.getMainLooper()).post { Toast.makeText(context, text, Toast.LENGTH_SHORT).show() }
    }
}
