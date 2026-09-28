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
        schedulePublish()
    }

    fun stop() {
        val app = appContext ?: return
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
        Link.notice(HandoffNotice(requestId, activity.title, "Sending to your PC…", failed = false, done = false))
        if (!Link.send(MessageTypes.HANDOFF_DELIVER, HandoffDeliverPayload(requestId, Link.deviceId, targetDeviceId, activity))) {
            Link.notice(HandoffNotice(requestId, activity.title, "Not connected to your PC.", failed = true, done = true))
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

        if (!Link.send(MessageTypes.HANDOFF_PULL, HandoffPullPayload(requestId, sourceDeviceId, Link.deviceId, activityId ?: known?.id, speculative, sentMode, choice))) {
            Link.notice(HandoffNotice(requestId, title, "Not connected to your PC.", failed = true, done = true))
            return
        }
        pending[requestId] = Pending(tapped, speculative || stream)
        Link.notice(HandoffNotice(requestId, title, "Continuing here…", failed = false, done = false))
        when {
            stream -> Launcher.start(context, StreamViewerActivity.intent(context, requestId, sourceDeviceId, known, tapped), title)
            speculative -> scope.launch {
                val result = runCatching { Openers.open(context, known!!, choice) }.getOrElse { OpenResult(HandoffStatus.Failed, it.message) }
                Log.i(TAG, "Opened '${known!!.title}' speculatively after ${SystemClock.elapsedRealtime() - tapped} ms: ${result.status}")
                if (result.status == HandoffStatus.Failed) pending[requestId] = Pending(tapped, opened = false)
            }
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
    private data class Pending(val tappedAt: Long, val opened: Boolean)

    private val pending = java.util.concurrent.ConcurrentHashMap<String, Pending>()
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
                if (deliver.targetDeviceId == Link.deviceId) scope.launch { openHere(deliver) }
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
                val text = when (result.status) {
                    HandoffStatus.Opened -> "Continuing on your PC"
                    HandoffStatus.Fallback -> result.detail ?: "Continuing on your PC"
                    HandoffStatus.Failed -> result.detail ?: "Couldn't continue it there."
                }
                Link.notice(HandoffNotice(result.requestId, "", text, failed, done = true))
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
        given: Activity? = null
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
            Link.notice(HandoffNotice(requestId, "Nothing to continue", text, failed = true, done = true))
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
        if (stream) {
            mirror(context, requestId, targetDeviceId, candidate, chosen)
            return
        }

        val activity = take(withFullUrl(context, candidate))
        Link.notice(HandoffNotice(requestId, activity.title, "Sending to your PC…", failed = false, done = false))
        if (!Link.send(MessageTypes.HANDOFF_DELIVER, HandoffDeliverPayload(requestId, Link.deviceId, targetDeviceId, activity, choice = chosen))) {
            Link.notice(HandoffNotice(requestId, activity.title, "Not connected to your PC.", failed = true, done = true))
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
        Link.send(MessageTypes.HANDOFF_DELIVER, HandoffDeliverPayload(requestId, Link.deviceId, targetDeviceId, activity, offer, choice))
        Link.notice(HandoffNotice(requestId, activity.title, "Showing it on your PC…", failed = false, done = true))
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
        if (!ContentLinks.isHostOnly(url)) return activity
        val full = BatonAccessibilityService.revealUrl(context, activity.app.id) ?: return activity
        Log.i(TAG, "Read the full address of ${activity.app.id}: $full")
        return activity.copy(url = full)
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
        val waiting = pending.remove(deliver.requestId)
        val startedAt = waiting?.tappedAt ?: SystemClock.elapsedRealtime()
        val stream = deliver.stream
        val result = when {
            stream != null && StreamViewerActivity.openSession == stream.sessionId -> OpenResult(HandoffStatus.Opened)
            stream != null -> {
                // The PC streams this window live over the media channel; watching it is how it continues here.
                Launcher.start(context, StreamViewerActivity.intent(context, stream.sessionId, deliver.sourceDeviceId, activity, startedAt, stream.width, stream.height), activity.title)
                OpenResult(HandoffStatus.Opened)
            }
            waiting?.opened == true -> {
                // Already open from the peer list: only correct the position if it drifted.
                reconcileSpeculative(activity)
                OpenResult(HandoffStatus.Opened)
            }
            else -> {
                Link.notice(HandoffNotice(deliver.requestId, activity.title, "Opening…", failed = false, done = false))
                runCatching { Openers.open(context, activity, deliver.choice) }.getOrElse { OpenResult(HandoffStatus.Failed, it.message ?: "Couldn't open it.") }
            }
        }
        val openedMs = SystemClock.elapsedRealtime() - startedAt
        Log.i(TAG, "Continued '${activity.title}' (${result.status}) ${openedMs} ms after the request")
        Link.send(MessageTypes.HANDOFF_RESULT,
            HandoffResultPayload(deliver.requestId, deliver.sourceDeviceId, Link.deviceId, result.status, result.detail, openedMs = openedMs))
        Link.notice(HandoffNotice(deliver.requestId, activity.title,
            result.detail ?: if (result.status == HandoffStatus.Failed) "Couldn't open it." else "Continuing here",
            failed = result.status == HandoffStatus.Failed, done = true))
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
