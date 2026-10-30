package dev.baton.android.notify

import android.app.Notification
import android.app.NotificationManager
import android.app.RemoteInput
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log
import dev.baton.android.BatonSettings
import dev.baton.android.handoff.Presence
import dev.baton.android.link.Link
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.NotificationKeyPayload
import dev.baton.android.protocol.NotificationPayload
import dev.baton.android.protocol.NotificationReplyPayload
import dev.baton.android.protocol.PresenceState
import dev.baton.android.protocol.Wire
import java.util.concurrent.ConcurrentHashMap

/**
 * This phone's notifications on the PC: each one that would make a sound or show a pop-up goes
 * to the PC as it is posted, and leaves there when it leaves here. The PC can reply (where the
 * app takes a reply) and dismiss. Nothing goes while the phone is in use, from apps turned off in
 * Settings, or what an app hides from the lock screen. A one-time code is marked, so the PC can
 * copy it.
 */
object PhoneNotifications {
    private const val TAG = "BatonNotifications"

    /** What the PC shows now, by key: only these are worth telling it about when they go. */
    private val shown = ConcurrentHashMap<String, String>()

    fun onPosted(listener: NotificationListenerService, sbn: StatusBarNotification) {
        val context = listener.applicationContext
        val skip = when {
            !BatonSettings.mirrorNotifications(context) -> "turned off"
            !Link.isReady -> "not connected"
            !worthShowing(listener, sbn) -> "not worth showing"
            sbn.packageName in BatonSettings.notificationsOffApps(context) -> "app turned off"
            // In use, the phone shows it itself.
            Presence.current(context) == PresenceState.Active -> "phone in use"
            else -> null
        }
        if (skip != null) {
            Log.d(TAG, "Kept ${sbn.packageName}'s notification here: $skip")
            return
        }

        val extras = sbn.notification.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty()
        if (title.isBlank() && text.isBlank()) return
        // An update that changes nothing the PC shows (progress ticks, a re-sort) stays here.
        val content = "$title\n$text"
        if (shown[sbn.key] == content) return
        shown[sbn.key] = content

        val payload = NotificationPayload(
            key = sbn.key,
            appId = sbn.packageName,
            appName = appName(context, sbn.packageName),
            title = title,
            text = text,
            postedAt = Wire.time(sbn.postTime),
            canReply = replyAction(sbn.notification) != null,
            code = OneTimeCodes.find("$title $text")
        )
        if (Link.send(MessageTypes.NOTIFICATION_POSTED, payload)) Log.i(TAG, "Showing ${sbn.packageName}'s notification on the PC")
        else shown.remove(sbn.key)
    }

    fun onRemoved(sbn: StatusBarNotification) {
        if (shown.remove(sbn.key) != null) Link.send(MessageTypes.NOTIFICATION_REMOVED, NotificationKeyPayload(sbn.key))
    }

    /** The PC dismissed it: dismiss it here too. */
    fun dismiss(key: String) {
        shown.remove(key)
        runCatching { dev.baton.android.handoff.BatonNotificationListener.instance?.cancelNotification(key) }
    }

    /** The PC replied: fill the app's reply field and send, as typing in the shade would. */
    fun reply(context: Context, payload: NotificationReplyPayload) {
        val listener = dev.baton.android.handoff.BatonNotificationListener.instance ?: return
        val sbn = runCatching { listener.activeNotifications }.getOrNull()?.firstOrNull { it.key == payload.key } ?: run {
            Log.w(TAG, "Reply to a notification that is gone")
            return
        }
        val action = replyAction(sbn.notification) ?: return
        val input = action.remoteInputs.first { it.allowFreeFormInput }
        val fill = Intent().addFlags(Intent.FLAG_RECEIVER_FOREGROUND)
        RemoteInput.addResultsToIntent(action.remoteInputs, fill, Bundle().apply { putCharSequence(input.resultKey, payload.text) })
        runCatching { action.actionIntent.send(context, 0, fill) }.onFailure { Log.w(TAG, "Reply failed", it) }
    }

    /** Something a person would want to see: not ongoing, silent, a group header or hidden from the lock screen. */
    private fun worthShowing(listener: NotificationListenerService, sbn: StatusBarNotification): Boolean {
        val notification = sbn.notification
        if (sbn.packageName == listener.packageName || sbn.isOngoing) return false
        if (notification.flags and (Notification.FLAG_GROUP_SUMMARY or Notification.FLAG_FOREGROUND_SERVICE) != 0) return false
        if (notification.visibility == Notification.VISIBILITY_SECRET) return false
        if (notification.category in setOf(Notification.CATEGORY_TRANSPORT, Notification.CATEGORY_SERVICE,
                Notification.CATEGORY_PROGRESS, Notification.CATEGORY_SYSTEM)) return false
        val ranking = NotificationListenerService.Ranking()
        val ranked = runCatching { listener.currentRanking.getRanking(sbn.key, ranking) }.getOrDefault(false)
        return !ranked || ranking.importance >= NotificationManager.IMPORTANCE_DEFAULT
    }

    private fun replyAction(notification: Notification): Notification.Action? =
        notification.actions?.firstOrNull { action -> action.remoteInputs?.any { it.allowFreeFormInput } == true }

    private fun appName(context: Context, packageName: String): String = runCatching {
        context.packageManager.getApplicationLabel(context.packageManager.getApplicationInfo(packageName, 0)).toString()
    }.getOrDefault(packageName)
}

/** One-time codes (2FA, logins, bank TANs) in a notification's words. */
object OneTimeCodes {
    private val keywords = Regex(
        "(?i)(code|otp|verif|passcode|\\bpin\\b|\\btan\\b|one[- ]time|2fa|einmal|bestätig|sicherheits|anmelde|login|kennwort)"
    )
    private val number = Regex("(?<![\\d.,:/-])(?:G-)?(\\d{3}[ -]\\d{3}|\\d{4,8})(?![\\d.,:/-])")

    /** The code, digits only; null when the words don't speak of a code or no number fits. */
    fun find(text: String): String? {
        if (!keywords.containsMatchIn(text)) return null
        val candidates = number.findAll(text).map { it.groupValues[1].filter(Char::isDigit) }.toList()
        // Six digits is the common length; a four-digit year or a long number elsewhere is not a code.
        return candidates.firstOrNull { it.length == 6 } ?: candidates.firstOrNull { it.length in 4..8 && !looksLikeYear(it) }
    }

    private fun looksLikeYear(digits: String) = digits.length == 4 && digits.toInt() in 1900..2099
}
