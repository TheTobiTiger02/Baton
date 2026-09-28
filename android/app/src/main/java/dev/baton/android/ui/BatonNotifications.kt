package dev.baton.android.ui

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import dev.baton.android.MainActivity
import dev.baton.android.R
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.link.Link
import dev.baton.android.link.LinkPhase
import dev.baton.android.protocol.PeerInfo

/**
 * Baton's notifications: the persistent link notification (which doubles as the quickest way to
 * hand something over), prompts to continue when Android blocks a direct launch, and failures.
 */
object BatonNotifications {
    const val LINK_ID = 1
    const val PROMPT_ID = 2
    private const val RESULT_ID = 3
    private const val SUGGESTION_ID = 4
    private const val CHANNEL_LINK = "link"
    private const val CHANNEL_CONTINUE = "continue"
    private const val CHANNEL_RESULTS = "results"

    fun link(context: Context): Notification {
        ensureChannels(context)
        val status = Link.status.value
        val pc = Link.peers.value.firstOrNull { it.kind == "pc" }
        val pcActivity = pc?.activities?.firstOrNull()
        val local = HandoffEngine.local.value.firstOrNull()

        val (title, text) = when {
            status.phase != LinkPhase.Ready -> "Baton" to status.detail.ifBlank { "Not connected" }
            pcActivity != null -> "On ${pc.name}: ${pcActivity.title}" to (pcActivity.subtitle ?: pcActivity.app.name)
            local != null -> "Ready to continue on ${pc?.name ?: "your PC"}" to local.title
            else -> "Connected to ${pc?.name ?: "your PC"}" to "Play something, then send it with one tap."
        }

        val builder = Notification.Builder(context, CHANNEL_LINK)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(activityIntent(context, 0, Intent(context, MainActivity::class.java)))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)

        if (status.phase == LinkPhase.Ready) {
            if (pcActivity != null && pc != null) {
                builder.addAction(action(context, 1, "Continue here", HandoffActionReceiver.pull(context, pc)))
            }
            if (local != null) {
                builder.addAction(action(context, 2, "Send to PC", HandoffActionReceiver.send(context)))
            }
        }
        return builder.build()
    }

    fun updateLink(context: Context) {
        context.getSystemService(NotificationManager::class.java).notify(LINK_ID, link(context))
    }

    /** Android blocked a direct start: ask the user to tap once instead. */
    fun continuePrompt(context: Context, intent: Intent, title: String) {
        ensureChannels(context)
        val pending = PendingIntent.getActivity(context, 10, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(context, CHANNEL_CONTINUE)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle("Continue $title")
            .setContentText("Tap to pick up where you left off.")
            .setContentIntent(pending)
            .setFullScreenIntent(pending, true)
            .setCategory(Notification.CATEGORY_RECOMMENDATION)
            .setAutoCancel(true)
            .build()
        context.getSystemService(NotificationManager::class.java).notify(PROMPT_ID, notification)
    }

    /** "Continue X from your PC?" with a one-tap action, shown when the phone is picked up. */
    fun suggestion(context: Context, pc: PeerInfo, activity: dev.baton.android.protocol.Activity) {
        ensureChannels(context)
        suggested = pc.deviceId to activity.id
        val pull = HandoffActionReceiver.pull(context, pc.copy(activities = listOf(activity)))
        val pending = PendingIntent.getBroadcast(context, 11, pull, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(context, CHANNEL_CONTINUE)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle("Continue ${activity.title}?")
            .setContentText("From ${pc.name}${activity.subtitle?.let { " · $it" } ?: ""}")
            .setContentIntent(pending)
            .addAction(Notification.Action.Builder(null, "Continue here", pending).build())
            .setAutoCancel(true)
            .setTimeoutAfter(60_000)
            .build()
        context.getSystemService(NotificationManager::class.java).notify(SUGGESTION_ID, notification)
    }

    /** The (device, activity) the current suggestion offers, until it is withdrawn. */
    @Volatile var suggested: Pair<String, String>? = null
        private set

    fun cancelSuggestion(context: Context) {
        suggested = null
        context.getSystemService(NotificationManager::class.java).cancel(SUGGESTION_ID)
    }

    fun result(context: Context, title: String, text: String) {
        ensureChannels(context)
        val notification = Notification.Builder(context, CHANNEL_RESULTS)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setTimeoutAfter(15_000)
            .build()
        context.getSystemService(NotificationManager::class.java).notify(RESULT_ID, notification)
    }

    private fun action(context: Context, code: Int, label: String, intent: Intent): Notification.Action {
        val pending = PendingIntent.getBroadcast(context, code, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Action.Builder(null, label, pending).build()
    }

    private fun activityIntent(context: Context, code: Int, intent: Intent) =
        PendingIntent.getActivity(context, code, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun ensureChannels(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_LINK) != null) return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_LINK, "Connection", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Shows what you can continue, and keeps Baton connected."
                setShowBadge(false)
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_CONTINUE, "Continue prompts", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Asks you to tap when Android doesn't let Baton open something by itself."
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_RESULTS, "Handoff problems", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Tells you when something couldn't be continued."
            }
        )
    }
}

/** Notification and tile buttons land here. */
class HandoffActionReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            ACTION_SEND -> HandoffEngine.sendTo()
            ACTION_PULL -> {
                context.getSystemService(NotificationManager::class.java).cancel(BatonNotifications.PROMPT_ID)
                BatonNotifications.cancelSuggestion(context)
                HandoffEngine.pull(
                    intent.getStringExtra(EXTRA_SOURCE) ?: Link.hostId,
                    intent.getStringExtra(EXTRA_ACTIVITY),
                    intent.getStringExtra(EXTRA_TITLE).orEmpty()
                )
            }
        }
    }

    companion object {
        const val ACTION_SEND = "dev.baton.android.SEND_TO_PC"
        const val ACTION_PULL = "dev.baton.android.CONTINUE_HERE"
        private const val EXTRA_SOURCE = "source"
        private const val EXTRA_ACTIVITY = "activity"
        private const val EXTRA_TITLE = "title"

        fun send(context: Context) = Intent(context, HandoffActionReceiver::class.java).setAction(ACTION_SEND)

        fun pull(context: Context, peer: PeerInfo) = Intent(context, HandoffActionReceiver::class.java)
            .setAction(ACTION_PULL)
            .putExtra(EXTRA_SOURCE, peer.deviceId)
            .putExtra(EXTRA_ACTIVITY, peer.activities.firstOrNull()?.id)
            .putExtra(EXTRA_TITLE, peer.activities.firstOrNull()?.title)
    }
}
