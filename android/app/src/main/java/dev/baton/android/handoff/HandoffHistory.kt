package dev.baton.android.handoff

import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.HandoffChoice
import dev.baton.android.protocol.HandoffStatus
import dev.baton.android.protocol.HandoffModes
import dev.baton.android.protocol.ChoiceKinds
import dev.baton.android.protocol.ActivityKind
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class HandoffIntent(val source: String, val target: String, val activityId: String?, val mode: String,
    val choice: HandoffChoice?, val activity: Activity?, val standalone: Boolean = false) {
    val streaming get() = when (choice?.kind) {
        ChoiceKinds.STREAM -> true
        ChoiceKinds.APP, ChoiceKinds.WEB -> false
        else -> mode == HandoffModes.STREAM || activity?.kind == ActivityKind.WindowStream ||
            activity?.let { it.content?.provider == "unknown" && it.window != null } == true
    }
    fun matches(current: Activity): Boolean = activity?.let { original ->
        if (original.url != null && current.url != null) original.url.substringBefore('#') == current.url.substringBefore('#')
        else original.title == current.title
    } ?: true
}

data class HandoffRecord(val requestId: String, val title: String, val startedAt: Long, val intent: HandoffIntent,
    val status: HandoffStatus? = null, val detail: String = "Sending…", val unconfirmed: Boolean = false) {
    val recoverable get() = status == HandoffStatus.Failed || unconfirmed
    val outcome get() = when {
        unconfirmed -> "No confirmation received"
        status == HandoffStatus.Opened -> "Opened on destination"
        status == HandoffStatus.Fallback -> "Fallback used"
        status == HandoffStatus.Failed -> "Couldn't continue"
        else -> "In progress"
    }
}

/** Process-local state, independent of link reconnects and screen lifetimes. */
class HandoffHistory {
    companion object { const val PENDING_MS = 120_000L }
    private val flow = MutableStateFlow<List<HandoffRecord>>(emptyList())
    private val cleared = linkedSetOf<String>()
    val recent = flow.asStateFlow()

    @Synchronized fun begin(id: String, title: String, intent: HandoffIntent, detail: String, now: Long = System.currentTimeMillis()) {
        if (id in cleared) return
        val old = find(id)
        if (old?.status != null || old?.unconfirmed == true) return
        put(HandoffRecord(id, title, old?.startedAt ?: now, old?.intent?.let { original -> original.copy(activityId = original.activityId ?: intent.activityId, activity = original.activity ?: intent.activity) } ?: intent, detail = detail))
    }

    @Synchronized fun progress(id: String, detail: String) {
        val old = find(id) ?: return
        if (old.status == null && !old.unconfirmed) put(old.copy(detail = detail))
    }

    @Synchronized fun finish(id: String, status: HandoffStatus, detail: String?): HandoffRecord? {
        val old = find(id) ?: return null
        if (old.status != null && !(old.intent.streaming && old.status != HandoffStatus.Failed && status == HandoffStatus.Failed)) return null
        return old.copy(status = status, detail = detail ?: when (status) {
            HandoffStatus.Opened -> "Destination reported the activity opened."
            HandoffStatus.Fallback -> "Destination used a fallback."
            HandoffStatus.Failed -> "Couldn't open this activity."
        }, unconfirmed = false).also(::put)
    }

    @Synchronized fun expire(now: Long): List<HandoffRecord> {
        val expired = flow.value.filter { it.status == null && !it.unconfirmed && now - it.startedAt >= PENDING_MS }
            .map { it.copy(unconfirmed = true, detail = "The activity may already have opened.") }
        expired.forEach(::put)
        return expired
    }

    @Synchronized fun find(id: String) = flow.value.firstOrNull { it.requestId == id }

    @Synchronized fun clear() {
        cleared.addAll(flow.value.map { it.requestId })
        while (cleared.size > 80) cleared.remove(cleared.first())
        flow.value = emptyList()
    }

    private fun put(record: HandoffRecord) {
        val ordered = (flow.value.filterNot { it.requestId == record.requestId } + record).sortedByDescending { it.startedAt }
        cleared.addAll(ordered.drop(40).map { it.requestId })
        while (cleared.size > 80) cleared.remove(cleared.first())
        flow.value = ordered.take(40)
    }
}
