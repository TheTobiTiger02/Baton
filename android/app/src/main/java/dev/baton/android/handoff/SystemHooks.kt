package dev.baton.android.handoff

import android.accessibilityservice.AccessibilityService
import android.app.KeyguardManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.PowerManager
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import dev.baton.android.protocol.PresenceState
import dev.baton.android.ui.BatonNotifications

/**
 * Baton's notification listener. It reads no notifications; the grant is what unlocks the phone's
 * media sessions ([MediaSessions]).
 */
class BatonNotificationListener : NotificationListenerService() {
    override fun onListenerConnected() {
        MediaSessions.start(this, ComponentName(this, BatonNotificationListener::class.java))
    }

    override fun onListenerDisconnected() {
        MediaSessions.stop()
    }

    companion object {
        fun isGranted(context: Context): Boolean =
            Settings.Secure.getString(context.contentResolver, "enabled_notification_listeners")
                ?.contains(ComponentName(context, BatonNotificationListener::class.java).flattenToString()) == true
    }
}

/**
 * Baton's accessibility service. It does two things: remembers the URL a browser is showing, so a
 * page can be continued on the PC, and, because Android exempts apps with a bound accessibility
 * service from its background-launch limits, lets a handoff from the PC open instantly.
 */
class BatonAccessibilityService : AccessibilityService() {
    override fun onServiceConnected() {
        instance = this
        dev.baton.android.mirror.PhoneMirror.inputChanged()
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        dev.baton.android.mirror.PhoneMirror.inputChanged()
        super.onDestroy()
    }

    override fun onInterrupt() = Unit

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        val packageName = event?.packageName?.toString() ?: return
        if (event.eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) {
            // The active window, not the event's: a keyboard or a toast raises events too, and
            // events that close together arrive merged into the last one.
            BrowserPages.onForeground(rootInActiveWindow?.packageName?.toString() ?: packageName)
        }

        val urlBarId = KnownApps.urlBarIds[packageName] ?: return
        val root = rootInActiveWindow ?: return
        val bar = root.findAccessibilityNodeInfosByViewId(urlBarId).firstOrNull() ?: return
        // While the user is typing, the bar holds their query, not the page's address.
        if (bar.isFocused) return
        val url = bar.text?.toString()?.let(ContentLinks::normalizeUrl) ?: return
        BrowserPages.onPage(packageName, url, pageTitle(root))
    }

    /** Chrome and its relatives expose the page title on the toolbar's content description. */
    private fun pageTitle(root: AccessibilityNodeInfo): String? =
        root.findAccessibilityNodeInfosByViewId("com.android.chrome:id/title").firstOrNull()?.text?.toString()

    companion object {
        @Volatile
        var instance: BatonAccessibilityService? = null
            private set(value) {
                field = value
                connectedFlow.value = value != null
            }

        private val connectedFlow = kotlinx.coroutines.flow.MutableStateFlow(false)

        /** Whether the service is bound right now, for screens that show it. */
        val connected: kotlinx.coroutines.flow.StateFlow<Boolean> get() = connectedFlow

        fun isGranted(context: Context): Boolean =
            Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
                ?.contains(ComponentName(context, BatonAccessibilityService::class.java).flattenToString()) == true
    }
}

/** Starts activities from the background, which Android only allows in certain situations. */
object Launcher {
    /**
     * Starts [intent] directly when Baton may (its accessibility service is bound, or the phone is
     * in use and Baton was granted "display over other apps"); otherwise posts a notification the
     * user taps to continue. Returns true when the activity was started directly.
     */
    fun start(context: Context, intent: Intent, title: String): Boolean {
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val accessibility = BatonAccessibilityService.instance
        val canStartDirectly = accessibility != null || Settings.canDrawOverlays(context)
        if (canStartDirectly && Presence.current(context) == PresenceState.Active) {
            val started = runCatching { (accessibility ?: context).startActivity(intent) }.isSuccess
            if (started) return true
        }

        BatonNotifications.continuePrompt(context, intent, title)
        return false
    }
}

object Presence {
    fun current(context: Context): PresenceState {
        val power = context.getSystemService(PowerManager::class.java)
        val keyguard = context.getSystemService(KeyguardManager::class.java)
        return when {
            power?.isInteractive != true -> PresenceState.Idle
            keyguard?.isKeyguardLocked == true -> PresenceState.Locked
            else -> PresenceState.Active
        }
    }
}
