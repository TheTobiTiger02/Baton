package dev.baton.android.handoff

import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

/**
 * Finds a YouTube video id from a title (and channel). Twin of the PC's resolver; used when the PC
 * reports a browser video by its media-session title only.
 */
object YouTubeResolver {
    private const val SEARCH_URL = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false"
    private val http = OkHttpClient.Builder().callTimeout(10, TimeUnit.SECONDS).build()

    data class Candidate(val id: String, val title: String, val channel: String, val durationMs: Long = 0)

    suspend fun findVideoId(title: String, channel: String?, durationMs: Long = 0): String? = withContext(Dispatchers.IO) {
        val body = buildJsonObject {
            putJsonObject("context") {
                putJsonObject("client") {
                    put("clientName", "WEB")
                    put("clientVersion", "2.20250901.00.00")
                    put("hl", "en")
                }
            }
            put("query", if (channel.isNullOrBlank()) title else "$title $channel")
        }.toString()
        runCatching {
            http.newCall(
                Request.Builder().url(SEARCH_URL).post(body.toRequestBody("application/json".toMediaType())).build()
            ).execute().use { response ->
                if (!response.isSuccessful) return@use null
                val root = kotlinx.serialization.json.Json.parseToJsonElement(response.body?.string().orEmpty())
                pick(videoRenderers(root).take(10).toList(), title, channel, durationMs)
            }
        }.getOrNull()
    }

    /**
     * The exact title (preferring the same channel) wins; otherwise the top result if its title is
     * close. When the length of what was playing is known, a candidate of another length is never
     * picked: a film's title also matches its trailer.
     */
    fun pick(all: List<Candidate>, title: String, channel: String?, durationMs: Long = 0): String? {
        val candidates = if (durationMs > 0) all.filter { sameLength(it.durationMs, durationMs) } else all
        val wanted = normalize(title)
        val exact = candidates.filter { normalize(it.title) == wanted }
        if (exact.isNotEmpty()) {
            return (exact.firstOrNull { channel != null && normalize(it.channel) == normalize(channel) } ?: exact.first()).id
        }
        val top = candidates.firstOrNull() ?: return null
        val topTitle = normalize(top.title)
        return if (wanted.isNotEmpty() && topTitle.isNotEmpty() && (topTitle.contains(wanted) || wanted.contains(topTitle))) top.id else null
    }

    private fun videoRenderers(node: JsonElement): Sequence<Candidate> = sequence {
        when (node) {
            is JsonObject -> {
                val video = node["videoRenderer"] as? JsonObject
                val id = (video?.get("videoId") as? JsonPrimitive)?.content
                if (video != null && id != null) {
                    yield(Candidate(id, runs(video["title"]), runs(video["ownerText"]), parseLength(runs(video["lengthText"]))))
                }
                node.values.forEach { yieldAll(videoRenderers(it)) }
            }
            is JsonArray -> node.forEach { yieldAll(videoRenderers(it)) }
            else -> Unit
        }
    }

    private fun runs(text: JsonElement?): String = runCatching {
        val obj = text as JsonObject
        ((obj["runs"] as? JsonArray)?.firstOrNull() as? JsonObject)?.get("text")?.jsonPrimitive?.content
            ?: obj["simpleText"]?.jsonPrimitive?.content
    }.getOrNull().orEmpty()

    fun sameLength(candidateMs: Long, expectedMs: Long): Boolean =
        candidateMs > 0 && kotlin.math.abs(candidateMs - expectedMs) <= maxOf(3_000L, expectedMs / 50)

    /** Reads "1:43:25" or "4:05" as milliseconds; 0 when absent (live streams). */
    fun parseLength(text: String): Long =
        text.split(':').fold(0L) { total, part -> total * 60 + (part.toLongOrNull() ?: return 0L) } * 1000

    private fun normalize(value: String): String = value.lowercase().filter { it.isLetterOrDigit() }
}
