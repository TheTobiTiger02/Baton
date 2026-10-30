package dev.baton.android.handoff

import java.util.Locale
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

    /** Shorts are at most three minutes long; their search results carry no length. */
    const val SHORTS_MAX_MS = 180_000L

    data class Candidate(val id: String, val title: String, val channel: String, val durationMs: Long = 0, val isShort: Boolean = false)

    /**
     * YouTube translates titles for each viewer, so a title the PC read in German only matches a
     * German search. This phone's language goes first; English catches titles left untranslated.
     */
    fun searchLanguages(preferred: String = Locale.getDefault().language): List<String> =
        listOf(preferred.lowercase(), "en").filter { it.length == 2 }.distinct()

    suspend fun findVideoId(title: String, channel: String?, durationMs: Long = 0): String? = withContext(Dispatchers.IO) {
        searchLanguages().firstNotNullOfOrNull { hl -> search(title, channel, hl)?.let { pick(it, title, channel, durationMs) } }
    }

    private fun search(title: String, channel: String?, hl: String): List<Candidate>? {
        val body = buildJsonObject {
            putJsonObject("context") {
                putJsonObject("client") {
                    put("clientName", "WEB")
                    put("clientVersion", "2.20250901.00.00")
                    put("hl", hl)
                }
            }
            put("query", if (channel.isNullOrBlank()) title else "$title $channel")
        }.toString()
        return runCatching {
            http.newCall(
                Request.Builder().url(SEARCH_URL).post(body.toRequestBody("application/json".toMediaType())).build()
            ).execute().use { response ->
                if (!response.isSuccessful) return@use null
                parse(kotlinx.serialization.json.Json.parseToJsonElement(response.body?.string().orEmpty()))
            }
        }.getOrNull()
    }

    /** The first ten videos in result order, then the first ten Shorts. */
    fun parse(root: JsonElement): List<Candidate> {
        val all = candidates(root).toList()
        return all.filter { !it.isShort }.take(10) + all.filter { it.isShort }.take(10)
    }

    /**
     * The exact title (preferring the same channel) wins; otherwise the top result if its title is
     * close. When the length of what was playing is known, a candidate of another length is never
     * picked: a film's title also matches its trailer. A Short has no length in the results, so it
     * only qualifies when what was playing is short enough to be one.
     */
    fun pick(all: List<Candidate>, title: String, channel: String?, durationMs: Long = 0): String? {
        val candidates = if (durationMs > 0) all.filter {
            sameLength(it.durationMs, durationMs) || (it.isShort && durationMs <= SHORTS_MAX_MS + 1_000)
        } else all
        val wanted = normalize(title)
        val exact = candidates.filter { normalize(it.title) == wanted }
        if (exact.isNotEmpty()) {
            return (exact.firstOrNull { channel != null && normalize(it.channel) == normalize(channel) } ?: exact.first()).id
        }
        val top = candidates.firstOrNull() ?: return null
        val topTitle = normalize(top.title)
        return if (wanted.isNotEmpty() && topTitle.isNotEmpty() && (topTitle.contains(wanted) || wanted.contains(topTitle))) top.id else null
    }

    private fun candidates(node: JsonElement): Sequence<Candidate> = sequence {
        when (node) {
            is JsonObject -> {
                val video = node["videoRenderer"] as? JsonObject
                val lockup = node["shortsLockupViewModel"] as? JsonObject
                val reel = node["reelItemRenderer"] as? JsonObject
                val videoId = text(video?.get("videoId"))
                val shortId = text(lockup?.path("onTap", "innertubeCommand", "reelWatchEndpoint", "videoId"))
                val reelId = text(reel?.get("videoId"))
                when {
                    video != null && videoId != null ->
                        yield(Candidate(videoId, runs(video["title"]), runs(video["ownerText"]), parseLength(runs(video["lengthText"]))))
                    lockup != null && shortId != null ->
                        yield(Candidate(shortId, text(lockup.path("overlayMetadata", "primaryText", "content")).orEmpty(), "", isShort = true))
                    reel != null && reelId != null -> yield(Candidate(reelId, runs(reel["headline"]), "", isShort = true))
                }
                node.values.forEach { yieldAll(candidates(it)) }
            }
            is JsonArray -> node.forEach { yieldAll(candidates(it)) }
            else -> Unit
        }
    }

    private fun JsonObject.path(vararg keys: String): JsonElement? =
        keys.fold(this as JsonElement?) { node, key -> (node as? JsonObject)?.get(key) }

    private fun text(node: JsonElement?): String? =
        (node as? JsonPrimitive)?.takeIf { it.isString }?.content?.takeIf { it.isNotEmpty() }

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
