package dev.baton.android.handoff

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import dev.baton.android.BatonSettings
import dev.baton.android.link.HandoffNotice
import dev.baton.android.link.Link
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.ActivityListPayload
import dev.baton.android.protocol.Envelope
import dev.baton.android.protocol.HandoffDeliverPayload
import dev.baton.android.protocol.HandoffModes
import dev.baton.android.protocol.HandoffChoice
import dev.baton.android.protocol.ChoiceKinds
import dev.baton.android.protocol.AppPreferencesPayload
import dev.baton.android.apps.AppCatalog
import dev.baton.android.apps.Choices
import dev.baton.android.ui.ChoicePrompt
import dev.baton.android.protocol.MediaCommandPayload
import dev.baton.android.protocol.StreamActions
import dev.baton.android.protocol.StreamControlPayload
import dev.baton.android.protocol.StreamKinds
import dev.baton.android.protocol.StreamOffer
import dev.baton.android.mirror.PhoneMirror
import dev.baton.android.protocol.HandoffPullPayload
import dev.baton.android.protocol.HandoffResultPayload
import dev.baton.android.protocol.HandoffStatus
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.PresenceState
import dev.baton.android.protocol.Wire
import dev.baton.android.protocol.Wire.read
import dev.baton.android.player.NowPlaying
import dev.baton.android.stream.StreamViewerActivity
import dev.baton.android.ui.BatonNotifications
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

import kotlinx.coroutines.async
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.delay
/**
 * The phone's half of a handoff: keeps the PC told what this phone could hand over, hands an
 * activity over on request (pausing it here), and continues activities handed to this phone.
 */
object HandoffEngine {
    private const val TAG = "BatonHandoff"
    private const val PUBLISH_DEBOUNCE_MS = 300L

    private val handler = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val localFlow = MutableStateFlow<List<Activity>>(emptyList())
    private var appContext: Context? = null
    private var presence = PresenceState.Active
    private val publish = Runnable { publishNow() }

    val history = HandoffHistory()
    private var expiry: kotlinx.coroutines.Job? = null
    private fun begin(id: String, activity: Activity?, source: String, target: String, mode: String, choice: HandoffChoice?,
        title: String = activity?.title ?: "Activity", standalone: Boolean = false, activityId: String? = activity?.id) {
        history.begin(id, title, HandoffIntent(source, target, activityId, mode, choice, activity?.copy(artworkJpegBase64 = null), standalone), "Requesting activity…")
    }

    private fun notice(value: HandoffNotice) {
        history.progress(value.requestId, value.text)
        Link.notice(value)
    }

    private fun finish(id: String, status: HandoffStatus, detail: String?) {
        val record = history.finish(id, status, detail) ?: return
        notice(HandoffNotice(id, record.title, record.detail, status == HandoffStatus.Failed, true))
    }
    fun fail(requestId: String, detail: String) = finish(requestId, HandoffStatus.Failed, detail)
    fun recovery(record: HandoffRecord): Triple<Boolean, Boolean, String?> {
        val intent = record.intent
        if (!Link.isReady || listOf(intent.source, intent.target).any { id ->
                id != Link.deviceId && Link.peers.value.none { it.deviceId == id && it.online } })
            return Triple(false, false, "Connect both devices to recover this handoff.")
        val activity = recoverActivity(intent) ?: return Triple(false, false, "The original activity is no longer available.")
        val streamable = if (intent.source == Link.deviceId) intent.target == Link.hostId
            else Link.peers.value.any { it.deviceId == intent.source && it.kind == "pc" } && activity.window != null
        return Triple(record.recoverable, streamable && (record.recoverable || record.status == HandoffStatus.Fallback), null)
    }

    private fun recoverActivity(intent: HandoffIntent): Activity? {
        if (intent.standalone) {
            val original = intent.activity ?: return null
            if (original.playback == null) return original
            val fresh = computeLocal().firstOrNull { it.app.id == original.app.id && it.title == original.title } ?: return null
            return original.copy(playback = fresh.playback, updatedAt = fresh.updatedAt)
        }
        if (intent.source == Link.deviceId) {
            return computeLocal().firstOrNull { it.id == intent.activityId && intent.matches(it) } ?: appContext?.let { context ->
                AppCatalog.apps(context).firstOrNull { "app:${it.id}" == intent.activityId }?.let { AppCatalog.activity(context, it) }
            }
        }
        return Link.peers.value.firstOrNull { it.deviceId == intent.source }?.activities?.firstOrNull { it.id == intent.activityId && intent.matches(it) }
    }
    fun recover(requestId: String, stream: Boolean = false) {
        val record = history.find(requestId) ?: return
        val allowed = recovery(record)
        if (!(if (stream) allowed.second else allowed.first)) return
        val intent = record.intent
        val activity = recoverActivity(intent) ?: return
        val mode = if (stream) HandoffModes.STREAM else intent.mode
        val choice = if (stream) HandoffChoice(ChoiceKinds.STREAM, "Stream") else intent.choice?.copy(remember = false)
        if (intent.source == Link.deviceId) {
            scope.launch { deliverLocal(Wire.newId(), intent.target, activity.id, mode, choice,
                given = activity.takeIf { intent.standalone }, standalone = intent.standalone) }
        } else pull(intent.source, activity.id, activity.title, mode, choice)
    }

    /** What this phone could hand over right now, most relevant first. */
    val local: StateFlow<List<Activity>> = localFlow.asStateFlow()

    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            schedulePublish()
            if (intent.action == Intent.ACTION_USER_PRESENT) suggestFromPc(context)
        }
    }

    private val suggestions = Suggestions()
    private var peersWatch: kotlinx.coroutines.Job? = null

    /**
     * Picked up the phone after leaving the PC (now idle or locked): offer what it was playing or
     * just showing (see [Suggestions.pick]) with one tap, once per [Suggestions.REPEAT_AFTER_MS].
     */
    private fun suggestFromPc(context: Context) {
        if (!BatonSettings.suggestFromPc(context)) return
        val pc = Link.peers.value.firstOrNull { it.kind == "pc" } ?: return
        // Unlocking the phone at the desk is common: nothing is offered while the PC is in use,
        // even when it plays something.
        if (pc.presence == PresenceState.Active) return
        val activity = Suggestions.pick(pc.activities, pc.presence) ?: return
        if (suggestions.tryOffer(pc.deviceId, activity)) BatonNotifications.suggestion(context, pc, activity)
    }

    /** The offer is stale once the user is back at the PC or the activity is gone there. */
    private fun withdrawStaleSuggestion(context: Context, peers: List<dev.baton.android.protocol.PeerInfo>) {
        val offered = BatonNotifications.suggested ?: return
        val pc = peers.firstOrNull { it.deviceId == offered.first }
        if (pc == null || pc.presence == PresenceState.Active || pc.activities.none { it.id == offered.second }) {
            BatonNotifications.cancelSuggestion(context)
        }
    }

    fun start(context: Context) {
        if (appContext != null) return
        val app = context.applicationContext
        appContext = app
        dev.baton.android.apps.Choices.packages = app.packageManager
        MediaSessions.onChanged = ::schedulePublish
        BrowserPages.onChanged = ::schedulePublish
        app.registerReceiver(screenReceiver, IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_SCREEN_OFF)
            addAction(Intent.ACTION_USER_PRESENT)
        })
        peersWatch = scope.launch { Link.peers.collect { withdrawStaleSuggestion(app, it) } }
        expiry = scope.launch {
            while (true) {
                delay(1_000)
                history.expire(System.currentTimeMillis()).forEach {
                    notice(HandoffNotice(it.requestId, it.title, "No confirmation received. ${it.detail}", false, true))
                }
            }
        }
        schedulePublish()
    }

    fun stop() {
        val app = appContext ?: return
        expiry?.cancel()
        expiry = null
        peersWatch?.cancel()
        peersWatch = null
        runCatching { app.unregisterReceiver(screenReceiver) }
        MediaSessions.onChanged = null
        BrowserPages.onChanged = null
        appContext = null
    }

    /** The session to the PC became ready: tell it everything at once. */
    fun onReady() = publishNow()

    /** Recomputes the local list; the UI calls this when it opens, since sessions may predate the listener. */
    fun refresh() = schedulePublish()

    /** Sends this phone's activity ([activityId], or the top one) to [targetDeviceId] (the PC by default). */
    fun sendTo(targetDeviceId: String = Link.hostId, activityId: String? = null, mode: String = HandoffModes.AUTO, choice: HandoffChoice? = null) {
        scope.launch { deliverLocal(Wire.newId(), targetDeviceId, activityId, mode, choice) }
    }

    /**
     * Continues [activity] (on [sourceDeviceId]) on [targetDeviceId] the way the user wants: with
     * the app they chose for it before, else after asking once which app (only when there is more
     * than one way). [ask] asks even when a choice is remembered, to change it.
     */
    fun continueWith(activity: Activity, sourceDeviceId: String, targetDeviceId: String, ask: Boolean = false) {
        val context = appContext ?: return
        scope.launch {
            val remembered = if (ask) null else Choices.remembered(activity, sourceDeviceId, targetDeviceId)
            val choice = remembered ?: run {
                val offered = Choices.options(activity, targetDeviceId)
                val options = offered?.options.orEmpty()
                // One tap continues it the best way; "Choose app" (ask) is where the options are.
                when {
                    options.isEmpty() -> null
                    !ask -> options.first()
                    else -> ChoicePrompt.ask(context, activity, targetDeviceId, options, offered?.remembered) ?: return@launch
                }
            }
            if (sourceDeviceId == Link.deviceId) {
                deliverLocal(Wire.newId(), targetDeviceId, activity.id, HandoffModes.AUTO, choice, given = activity)
            } else {
                pull(sourceDeviceId, activity.id, activity.title, HandoffModes.AUTO, choice)
            }
        }
    }

    /** Hands over an activity already frozen by the caller, e.g. a player that is about to close. */
    fun send(activity: Activity, targetDeviceId: String = Link.hostId) {
        val requestId = Wire.newId()
        begin(requestId, activity, Link.deviceId, targetDeviceId, HandoffModes.AUTO, null, standalone = true)
        notice(HandoffNotice(requestId, activity.title, "Sending to your PC…", failed = false, done = false))
        if (!Link.send(MessageTypes.HANDOFF_DELIVER, HandoffDeliverPayload(requestId, Link.deviceId, targetDeviceId, activity))) {
            finish(requestId, HandoffStatus.Failed, "Not connected to your PC.")
        }
    }

    /**
     * Asks [sourceDeviceId] to hand its activity ([activityId], or its top one) to this phone.
     *
     * The activity is already known from the peer list, so it opens here at once (speculatively)
     * while the source pauses it and sends the exact position; that position then only corrects a
     * difference. A stream opens its viewer at once too, and the first frame follows.
     */
    fun pull(sourceDeviceId: String, activityId: String?, title: String, mode: String = HandoffModes.AUTO, choice: HandoffChoice? = null) {
        val context = appContext ?: return
        val requestId = Wire.newId()
        val tapped = SystemClock.elapsedRealtime()
        val known = Link.peers.value.firstOrNull { it.deviceId == sourceDeviceId }?.activities
            ?.let { list -> if (activityId == null) list.firstOrNull() else list.firstOrNull { it.id == activityId } }
        val stream = when (choice?.kind) {
            ChoiceKinds.STREAM -> true
            ChoiceKinds.APP, ChoiceKinds.WEB -> false
            else -> mode == HandoffModes.STREAM || (known != null && continuesAsStream(known))
        }
        // An app or site the user picked opens straight away: it needs nothing from the other device.
        val speculative = !stream && known != null &&
            (known.kind in SPECULATIVE_KINDS || choice?.kind == ChoiceKinds.APP || choice?.kind == ChoiceKinds.WEB)
        val sentMode = if (stream) HandoffModes.STREAM else mode

        begin(requestId, known, sourceDeviceId, Link.deviceId, sentMode, choice, title, activityId = activityId ?: known?.id)
        val opening = if (speculative && Link.isReady) scope.async {
            runCatching { Openers.open(context, known!!, choice) }.getOrElse { OpenResult(HandoffStatus.Failed, it.message) }
        } else null
        pending[requestId] = Pending(tapped, stream, opening)
        if (pending.size > 40) pending.entries.sortedByDescending { it.value.tappedAt }.drop(40).forEach { pending.remove(it.key) }
        if (!Link.send(MessageTypes.HANDOFF_PULL, HandoffPullPayload(requestId, sourceDeviceId, Link.deviceId, activityId ?: known?.id, speculative, sentMode, choice))) {
            opening?.cancel()
            pending.remove(requestId)
            finish(requestId, HandoffStatus.Failed, "Not connected to your PC.")
            return
        }
        notice(HandoffNotice(requestId, title, "Continuing here…", failed = false, done = false))
        when {
            stream -> Launcher.start(context, StreamViewerActivity.intent(context, requestId, sourceDeviceId, known, tapped), title)
        }
    }

    private fun runCommand(command: MediaCommandPayload) {
        val activity = computeLocal().firstOrNull { it.id == command.activityId }
        val done = when {
            activity == null -> false
            activity.kind == ActivityKind.LocalMedia -> NowPlaying.command(command.action, command.positionMs, command.volume)
            else -> MediaSessions.command(activity.app.id, command.action, command.positionMs, command.volume)
        }
        Log.i(TAG, "Remote ${command.action} on '${activity?.title}': $done")
        schedulePublish()
    }

    /** Plays, pauses, seeks or sets the volume of [activityId] on [ownerDeviceId], without moving it. */
    fun command(ownerDeviceId: String, activityId: String, action: String, positionMs: Long? = null, volume: Double? = null) {
        Link.send(MessageTypes.MEDIA_COMMAND, MediaCommandPayload(ownerDeviceId, activityId, action, positionMs, volume))
    }

    /** Matches the PC's rule: windows, and media only the PC can play, continue as a stream. */
    private fun continuesAsStream(activity: Activity) =
        activity.kind == ActivityKind.WindowStream || (activity.content?.provider == "unknown" && activity.window != null)

    /** A pull this phone is waiting on, and whether it already opened the activity. */
    private data class Pending(val tappedAt: Long, val streamOpened: Boolean, val opening: Deferred<OpenResult>? = null)
    private val pending = java.util.concurrent.ConcurrentHashMap<String, Pending>()
    private val opening = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
    private val SPECULATIVE_KINDS = setOf(ActivityKind.AppMedia, ActivityKind.WebMedia, ActivityKind.WebPage)

    fun onMessage(envelope: Envelope) {
        when (envelope.type) {
            MessageTypes.HANDOFF_PULL -> {
                val pull = envelope.read<HandoffPullPayload>()
                if (pull.sourceDeviceId == Link.deviceId) {
                    scope.launch { deliverLocal(pull.requestId, pull.targetDeviceId, pull.activityId, pull.mode, pull.choice) }
                }
            }
            MessageTypes.HANDOFF_DELIVER -> {
                val deliver = envelope.read<HandoffDeliverPayload>()
                if (deliver.targetDeviceId == Link.deviceId && opening.add(deliver.requestId)) scope.launch {
                    try { openHere(deliver) }
                    catch (error: Exception) {
                        finish(deliver.requestId, HandoffStatus.Failed, error.message ?: "Couldn't open it.")
                        Link.send(MessageTypes.HANDOFF_RESULT, HandoffResultPayload(deliver.requestId, deliver.sourceDeviceId,
                            Link.deviceId, HandoffStatus.Failed, error.message))
                    }
                    finally { opening.remove(deliver.requestId) }
                }
            }
            MessageTypes.STREAM_CONTROL -> {
                val control = envelope.read<StreamControlPayload>()
                if (control.sessionId == PhoneMirror.sessionId && control.action == StreamActions.STOP) {
                    appContext?.let { PhoneMirror.stop(it) }
                }
            }
            MessageTypes.HANDOFF_OPTIONS -> Choices.onOptions(envelope.read())
            MessageTypes.APP_PREFERENCES -> Choices.onPreferences(envelope.read<AppPreferencesPayload>().preferences)
            MessageTypes.MEDIA_COMMAND -> {
                val command = envelope.read<MediaCommandPayload>()
                if (command.ownerDeviceId == Link.deviceId) handler.post { runCommand(command) }
            }
            MessageTypes.HANDOFF_RESULT -> {
                val result = envelope.read<HandoffResultPayload>()
                val failed = result.status == HandoffStatus.Failed
                val text = result.detail ?: "Couldn't continue it there."
                val record = history.find(result.requestId) ?: return
                if (record.intent.source != result.sourceDeviceId || record.intent.target != result.targetDeviceId) return
                if (record.status != null && !(record.intent.streaming && record.status != HandoffStatus.Failed && failed)) return
                pending.remove(result.requestId)
                finish(result.requestId, result.status, result.detail ?: "Destination reported the activity opened.")
                if (failed) BatonNotifications.result(appContext ?: return, "Couldn't continue", text)
            }
        }
    }

    private suspend fun deliverLocal(
        requestId: String,
        targetDeviceId: String,
        activityId: String?,
        mode: String = HandoffModes.AUTO,
        choice: HandoffChoice? = null,
        given: Activity? = null,
        standalone: Boolean = false
    ) {
        val context = appContext ?: return
        val list = computeLocal()
        val candidate = given ?: if (activityId == null) list.firstOrNull() else list.firstOrNull { it.id == activityId }
            ?: activityId?.takeIf { it.startsWith("app:") }?.let { id ->
                // An app picked from the list, not the one in front.
                AppCatalog.apps(context).firstOrNull { "app:${it.id}" == id }?.let { AppCatalog.activity(context, it) }
            }
        if (candidate == null) {
            val text = "Nothing is playing or open on this phone right now."
            begin(requestId, null, Link.deviceId, targetDeviceId, mode, choice, "Nothing to continue", activityId = activityId)
            finish(requestId, HandoffStatus.Failed, text)
            Link.send(MessageTypes.HANDOFF_RESULT, HandoffResultPayload(requestId, Link.deviceId, targetDeviceId, HandoffStatus.Failed, text))
            return
        }

        // Without a choice (the tile, a hotkey on the PC), an app uses what was chosen for it, or its best match.
        val chosen = choice ?: Choices.remembered(candidate, Link.deviceId, targetDeviceId)
            ?: if (mode == HandoffModes.AUTO && candidate.kind == ActivityKind.WindowStream) {
                Choices.options(candidate, targetDeviceId)?.let { it.remembered ?: it.options.firstOrNull() }
            } else null
        val stream = when (chosen?.kind) {
            ChoiceKinds.STREAM -> true
            ChoiceKinds.APP, ChoiceKinds.WEB -> false
            else -> mode == HandoffModes.STREAM || candidate.kind == ActivityKind.WindowStream
        }
        begin(requestId, candidate, Link.deviceId, targetDeviceId, mode, chosen, standalone = standalone)
        val activity = try {
            if (stream) {
                mirror(context, requestId, targetDeviceId, candidate, chosen)
                return
            }
            take(withFullUrl(context, candidate))
        } catch (error: Exception) {
            if (error is kotlinx.coroutines.CancellationException) throw error
            val detail = error.message ?: "Couldn't obtain a fresh activity snapshot."
            finish(requestId, HandoffStatus.Failed, detail)
            Link.send(MessageTypes.HANDOFF_RESULT, HandoffResultPayload(requestId, Link.deviceId, targetDeviceId, HandoffStatus.Failed, detail))
            return
        }
        notice(HandoffNotice(requestId, activity.title, "Sending to your PC…", failed = false, done = false))
        if (!Link.send(MessageTypes.HANDOFF_DELIVER, HandoffDeliverPayload(requestId, Link.deviceId, targetDeviceId, activity, choice = chosen))) {
            finish(requestId, HandoffStatus.Failed, "Not connected to your PC.")
        }
        Log.i(TAG, "Handed ${activity.kind} '${activity.title}' to $targetDeviceId")
        schedulePublish()
        BatonNotifications.updateLink(context)
    }

    /**
     * Continues [activity] on the PC by showing this phone's screen there. The PC opens its window
     * at once; the picture follows as soon as the screen-sharing prompt here is accepted.
     */
    private fun mirror(context: Context, requestId: String, targetDeviceId: String, activity: Activity, choice: HandoffChoice? = null) {
        val (width, height) = PhoneMirror.displaySize(context).let { PhoneMirror.encodedSize(it.first, it.second) }
        val offer = StreamOffer(requestId, width, height, activity.title, StreamKinds.PHONE)
        if (!Link.send(MessageTypes.HANDOFF_DELIVER, HandoffDeliverPayload(requestId, Link.deviceId, targetDeviceId, activity, offer, choice))) {
            finish(requestId, HandoffStatus.Failed, "Not connected to your PC.")
            return
        }
        notice(HandoffNotice(requestId, activity.title, "Opening viewer; screen sharing needs consent…", failed = false, done = false))
        PhoneMirror.start(context, requestId, targetDeviceId, activity.title, activity.app.id.takeIf { activity.kind != ActivityKind.LocalMedia })
        Log.i(TAG, "Mirroring '${activity.title}' to $targetDeviceId")
    }

    /** Freezes the activity at this instant and pauses it here, so it continues where it stopped. */
    /**
     * A page whose browser shows only the site (Samsung Internet) gets its full address read
     * now, so the PC opens that page and not the site's home page.
     */
    private suspend fun withFullUrl(context: Context, activity: Activity): Activity {
        val url = activity.url ?: return activity
        // Where the page is scrolled to, while it is still in front and untouched.
        val anchored = if (activity.kind == ActivityKind.WebPage) {
            BatonAccessibilityService.readAnchor(activity.app.id)?.let { activity.copy(textAnchor = it) } ?: activity
        } else activity
        if (!ContentLinks.isHostOnly(url)) return anchored
        val full = BatonAccessibilityService.revealUrl(context, activity.app.id) ?: return anchored
        Log.i(TAG, "Read the full address of ${activity.app.id}: $full")
        return anchored.copy(url = full)
    }

    private fun take(activity: Activity): Activity {
        // A page sent from the browser: whatever the browser plays (a video in that page) stops too.
        if (activity.kind == ActivityKind.WebPage) MediaSessions.pause(activity.app.id)
        val playback = activity.playback ?: return activity
        val now = System.currentTimeMillis()
        when (activity.kind) {
            ActivityKind.AppMedia, ActivityKind.WebMedia -> MediaSessions.pause(activity.app.id)
            ActivityKind.LocalMedia -> NowPlaying.pause()
            else -> Unit
        }
        return activity.copy(playback = playback.copy(positionMs = playback.positionAt(now), playing = false, capturedAt = Wire.time(now)))
    }

    private suspend fun openHere(deliver: HandoffDeliverPayload) {
        val context = appContext ?: return
        val activity = deliver.activity
        val existing = history.find(deliver.requestId)
        if (existing != null && (existing.intent.source != deliver.sourceDeviceId || existing.intent.target != deliver.targetDeviceId)) return
        if (existing?.status != null) {
            Link.send(MessageTypes.HANDOFF_RESULT, HandoffResultPayload(deliver.requestId, deliver.sourceDeviceId,
                Link.deviceId, existing.status, existing.detail))
            return
        }
        begin(deliver.requestId, activity, deliver.sourceDeviceId, Link.deviceId,
            if (deliver.stream == null) HandoffModes.AUTO else HandoffModes.STREAM, deliver.choice)
        val waiting = pending.remove(deliver.requestId)
        val speculativeResult = waiting?.opening?.await()
        val startedAt = waiting?.tappedAt ?: SystemClock.elapsedRealtime()
        val stream = deliver.stream
        val result = when {
            stream != null && StreamViewerActivity.openSession == stream.sessionId -> OpenResult(HandoffStatus.Opened, "Stream viewer opened; waiting for picture.")
            stream != null -> {
                // The PC streams this window live over the media channel; watching it is how it continues here.
                if (Launcher.start(context, StreamViewerActivity.intent(context, stream.sessionId, deliver.sourceDeviceId, activity, startedAt, stream.width, stream.height), activity.title))
                    OpenResult(HandoffStatus.Opened, "Stream viewer opened; waiting for picture.")
                else OpenResult(HandoffStatus.Fallback, "Tap the notification to open the stream viewer; no picture is confirmed yet.")
            }
            speculativeResult != null && speculativeResult.status != HandoffStatus.Failed -> {
                // Already open from the peer list: only correct the position if it drifted.
                reconcileSpeculative(activity)
                OpenResult(speculativeResult.status, if (speculativeResult.status == HandoffStatus.Opened && activity.playback != null)
                    "App opened; playback position reconciliation requested, not confirmed." else speculativeResult.detail)
            }
            else -> {
                notice(HandoffNotice(deliver.requestId, activity.title, "Opening…", failed = false, done = false))
                runCatching { Openers.open(context, activity, deliver.choice) }.getOrElse { OpenResult(HandoffStatus.Failed, it.message ?: "Couldn't open it.") }
            }
        }
        val openedMs = SystemClock.elapsedRealtime() - startedAt
        Log.i(TAG, "Continued '${activity.title}' (${result.status}) ${openedMs} ms after the request")
        Link.send(MessageTypes.HANDOFF_RESULT,
            HandoffResultPayload(deliver.requestId, deliver.sourceDeviceId, Link.deviceId, result.status, result.detail, openedMs = openedMs))
        finish(deliver.requestId, result.status, result.detail)
    }

    private fun reconcileSpeculative(activity: Activity) {
        val playback = activity.playback ?: return
        scope.launch {
            MediaSessions.reconcile(
                matches = { controller ->
                    Openers.titlesMatch(controller.metadata?.getString(android.media.MediaMetadata.METADATA_KEY_TITLE), activity.title)
                },
                positionMs = playback.positionAt(),
                play = true,
                timeoutMs = 15_000,
                toleranceMs = 2_000
            )
        }
    }

    private fun schedulePublish() {
        handler.removeCallbacks(publish)
        handler.postDelayed(publish, PUBLISH_DEBOUNCE_MS)
    }

    private fun publishNow() {
        val context = appContext ?: return
        val list = computeLocal()
        presence = Presence.current(context)
        localFlow.value = list
        Link.send(MessageTypes.ACTIVITY_LIST, ActivityListPayload(list, presence))
        BatonNotifications.updateLink(context)
    }

    /** Media sessions first (browser ones get the page's URL), then the last browser page. */
    private fun computeLocal(): List<Activity> {
        val context = appContext ?: return emptyList()
        val deviceId = Link.deviceId
        val playing = NowPlaying.snapshot()
        val media = MediaSessions.activities(deviceId).map { activity ->
            if (activity.kind == ActivityKind.WebMedia) {
                BrowserPages.page(activity.app.id)?.let { activity.copy(url = it.url) } ?: activity
            } else {
                activity
            }
        }
        val covered = media.filter { it.kind == ActivityKind.WebMedia }.map { it.app.id }.toSet()
        val page = BrowserPages.activity(deviceId, { MediaSessions.appName(context, it) }, covered)
        // The app in front goes last: only apps with nothing better to offer continue as a mirror.
        val app = CurrentApp.activity(context, deviceId)?.takeIf { current ->
            (media + listOfNotNull(page)).none { it.app.id == current.app.id }
        }
        return (listOfNotNull(playing) + media + listOfNotNull(page) + listOfNotNull(app))
            .sortedWith(compareByDescending<Activity> { it.playback?.playing == true }
                .thenByDescending { it.kind != ActivityKind.WebPage }
                .thenByDescending { Wire.parseTime(it.updatedAt) })
    }
}
