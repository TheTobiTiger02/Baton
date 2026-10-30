package dev.baton.android.link

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.app.usage.UsageStatsManager
import android.content.Context
import android.os.Build
import android.os.PowerManager
import android.util.Log
import dev.baton.android.BuildConfig
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** One line of the link log. A run of identical events is kept as one entry with a [count]. */
data class LinkEvent(val at: Long, val event: String, val detail: String = "", val count: Int = 1, val lastAt: Long = at) {
    fun encode(): String = listOf(at, event, detail.replace('\t', ' ').replace('\n', ' '), count, lastAt).joinToString("\t")

    fun repeats(other: LinkEvent): Boolean = event == other.event && detail == other.detail

    companion object {
        fun decode(line: String): LinkEvent? {
            val parts = line.split('\t')
            val at = parts.getOrNull(0)?.toLongOrNull() ?: return null
            val event = parts.getOrNull(1)?.takeIf { it.isNotBlank() } ?: return null
            return LinkEvent(at, event, parts.getOrNull(2).orEmpty(),
                parts.getOrNull(3)?.toIntOrNull() ?: 1, parts.getOrNull(4)?.toLongOrNull() ?: at)
        }

        /** Appends [event] to [events], folding it into the last entry when it repeats it. */
        fun append(events: List<LinkEvent>, event: LinkEvent, capacity: Int): List<LinkEvent> {
            val last = events.lastOrNull()
            val next = if (last != null && last.repeats(event)) {
                events.dropLast(1) + last.copy(count = last.count + event.count, lastAt = event.lastAt)
            } else {
                events + event
            }
            return if (next.size > capacity) next.drop(next.size - capacity) else next
        }

        /**
         * Why the link went down since [since], most common first. Drops are the `drop.*` events;
         * process exits count by their reason, since "killed while idle" and "crashed" need
         * different fixes. Exits for an app update are expected and left out.
         */
        fun causes(events: List<LinkEvent>, since: Long): List<Pair<String, Int>> = events
            .filter { it.lastAt >= since && (it.event.startsWith("drop.") || isUnexpectedExit(it)) }
            .groupBy { if (it.event == "process.exit") "process.exit ${it.detail.substringBefore(' ')}" else it.event }
            .map { (cause, entries) -> cause to entries.sumOf { it.count } }
            .sortedWith(compareByDescending<Pair<String, Int>> { it.second }.thenBy { it.first })

        fun isUnexpectedExit(event: LinkEvent): Boolean =
            event.event == "process.exit" && !event.detail.startsWith("package-updated")
    }
}

/**
 * The phone's record of the link: every connect, drop, network change, Doze change and process
 * death, kept on disk so it survives the process being killed - which is the case it most needs to
 * explain. Its report is what to send when the link keeps dropping.
 */
object LinkDiagnostics {
    const val CAPACITY = 500
    private const val TAG = "BatonLinkLog"
    private const val FILE_NAME = "link-events.log"
    private const val KEY_LAST_EXIT = "last_recorded_exit"

    private val writer = Executors.newSingleThreadExecutor()
    private val flow = MutableStateFlow<List<LinkEvent>>(emptyList())
    val events: StateFlow<List<LinkEvent>> = flow.asStateFlow()

    private var file: File? = null
    private var linesInFile = 0

    @Synchronized
    fun init(context: Context) {
        if (file != null) return
        val log = File(context.applicationContext.filesDir, FILE_NAME)
        val loaded = runCatching { log.readLines() }.getOrDefault(emptyList())
        file = log
        linesInFile = loaded.size
        var events = emptyList<LinkEvent>()
        (loaded.mapNotNull(LinkEvent::decode) + flow.value).forEach { events = LinkEvent.append(events, it, CAPACITY) }
        flow.value = events
        recordExits(context.applicationContext)
    }

    @Synchronized
    fun record(event: String, detail: String = "", at: Long = System.currentTimeMillis()) {
        val entry = LinkEvent(at, event, detail.take(300))
        Log.i(TAG, "$event $detail")
        val next = LinkEvent.append(flow.value, entry, CAPACITY)
        flow.value = next
        val log = file ?: return
        linesInFile++
        // Appending is cheap; the file is rewritten from memory (folded and trimmed) now and then.
        val rewrite = if (linesInFile > CAPACITY * 2) next.also { linesInFile = it.size } else null
        writer.execute {
            runCatching {
                if (rewrite != null) log.writeText(rewrite.joinToString("") { it.encode() + "\n" })
                else log.appendText(entry.encode() + "\n")
            }.onFailure { Log.w(TAG, "Could not write the link log.", it) }
        }
    }

    @Synchronized
    fun clear() {
        flow.value = emptyList()
        linesInFile = 0
        val log = file ?: return
        writer.execute { runCatching { log.writeText("") } }
    }

    /** Battery state that decides whether Android lets the link live, in one line. */
    fun powerSummary(context: Context): String {
        val power = context.getSystemService(PowerManager::class.java)
        val unrestricted = runCatching { power.isIgnoringBatteryOptimizations(context.packageName) }.getOrDefault(false)
        val bucket = runCatching {
            standbyBucket(context.getSystemService(UsageStatsManager::class.java).appStandbyBucket)
        }.getOrDefault("unknown")
        return listOf(
            if (unrestricted) "battery unrestricted" else "battery optimized",
            "standby $bucket",
            if (power.isPowerSaveMode) "battery saver on" else null,
            if (power.isDeviceIdleMode) "dozing" else null
        ).filterNotNull().joinToString(", ")
    }

    fun report(context: Context): String = buildString {
        val now = System.currentTimeMillis()
        val all = events.value.sortedBy { it.at }
        appendLine("Baton link report, ${stamp(now)}")
        appendLine("Baton ${BuildConfig.VERSION_NAME}, Android ${Build.VERSION.RELEASE} (SDK ${Build.VERSION.SDK_INT}), ${Build.MANUFACTURER} ${Build.MODEL}")
        val status = Link.status.value
        appendLine("Link: ${status.phase}${if (status.detail.isNotBlank()) " - ${status.detail}" else ""}")
        appendLine("Power: ${powerSummary(context)}")
        TrustStore(context).host?.let { host ->
            appendLine("PC: ${host.pcName}")
            EndpointStore(context).ordered(host.hostId).forEach {
                val success = if (it.lastSuccessAt > 0) "last worked ${stamp(it.lastSuccessAt)}" else "never worked"
                appendLine("  ${it.kind} ${it.host}:${it.port}, $success, ${it.consecutiveFailures} failures in a row")
            }
        }
        val causes = LinkEvent.causes(all, now - DAY_MS)
        appendLine("Drops in the last 24 hours: " +
            if (causes.isEmpty()) "none" else causes.joinToString(", ") { "${it.first} ×${it.second}" })
        appendLine()
        all.forEach { appendLine(line(it)) }
    }

    fun line(event: LinkEvent): String {
        val repeated = if (event.count > 1) " (×${event.count}, last ${stamp(event.lastAt)})" else ""
        return "${stamp(event.at)} ${event.event} ${event.detail}".trimEnd() + repeated
    }

    private fun stamp(at: Long) = SimpleDateFormat("MM-dd HH:mm:ss", Locale.US).format(Date(at))

    /** Records how this app's process ended since the last time, so a silent kill shows up. */
    private fun recordExits(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return
        val preferences = context.getSharedPreferences("baton_link", Context.MODE_PRIVATE)
        val seen = preferences.getLong(KEY_LAST_EXIT, 0L)
        val exits = runCatching {
            context.getSystemService(ActivityManager::class.java).getHistoricalProcessExitReasons(context.packageName, 0, 10)
        }.getOrDefault(emptyList())
        exits.filter { it.timestamp > seen }.sortedBy { it.timestamp }.forEach {
            record("process.exit", "${exitReason(it.reason)} ${it.description.orEmpty()}".trim(), it.timestamp)
        }
        exits.maxOfOrNull { it.timestamp }?.takeIf { it > seen }?.let { preferences.edit().putLong(KEY_LAST_EXIT, it).apply() }
    }

    private fun exitReason(reason: Int): String = when (reason) {
        ApplicationExitInfo.REASON_LOW_MEMORY -> "low-memory"
        ApplicationExitInfo.REASON_CRASH -> "crash"
        ApplicationExitInfo.REASON_CRASH_NATIVE -> "native-crash"
        ApplicationExitInfo.REASON_ANR -> "anr"
        ApplicationExitInfo.REASON_EXIT_SELF -> "exit-self"
        ApplicationExitInfo.REASON_SIGNALED -> "signaled"
        ApplicationExitInfo.REASON_USER_REQUESTED -> "user-requested"
        ApplicationExitInfo.REASON_USER_STOPPED -> "user-stopped"
        ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE -> "excessive-resource-use"
        ApplicationExitInfo.REASON_FREEZER -> "freezer"
        ApplicationExitInfo.REASON_PACKAGE_UPDATED -> "package-updated"
        ApplicationExitInfo.REASON_PACKAGE_STATE_CHANGE -> "package-state-change"
        ApplicationExitInfo.REASON_PERMISSION_CHANGE -> "permission-change"
        ApplicationExitInfo.REASON_DEPENDENCY_DIED -> "dependency-died"
        ApplicationExitInfo.REASON_INITIALIZATION_FAILURE -> "initialization-failure"
        else -> "other"
    }

    private fun standbyBucket(bucket: Int): String = when (bucket) {
        UsageStatsManager.STANDBY_BUCKET_ACTIVE -> "active"
        UsageStatsManager.STANDBY_BUCKET_WORKING_SET -> "working-set"
        UsageStatsManager.STANDBY_BUCKET_FREQUENT -> "frequent"
        UsageStatsManager.STANDBY_BUCKET_RARE -> "rare"
        UsageStatsManager.STANDBY_BUCKET_RESTRICTED -> "restricted"
        5 -> "exempted"
        else -> bucket.toString()
    }

    private const val DAY_MS = 24L * 60L * 60L * 1000L
}
