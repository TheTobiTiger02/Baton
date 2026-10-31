package dev.baton.android

import android.content.Context

/** This phone's own switches. */
object BatonSettings {
    private const val FILE = "baton_settings"
    private const val SUGGEST_FROM_PC = "suggest_from_pc"
    private const val SYNC_CLIPBOARD = "sync_clipboard"
    private const val WAKE_RELAY = "wake_relay"
    private const val NEARBY = "nearby"
    private const val MIRROR_MODE = "mirror_mode"
    private const val WIRELESS_DEBUGGING_OFF = "wireless_debugging_off"
    private const val REVOKE_PENDING = "revoke_pending"
    private const val SWIPES = "swipes"
    private const val SWIPES_OFF_APPS = "swipes_off_apps"
    private const val MIRROR_NOTIFICATIONS = "mirror_notifications"
    private const val NOTIFICATIONS_OFF_APPS = "notifications_off_apps"
    /** How showing this phone on the PC gets Android's screen-sharing consent. */
    enum class MirrorMode {
        /** Android asks on every handoff. */
        AskEachTime,

        /** Android asks once; the capture stays ready (and the cast icon shown) between handoffs. */
        KeepReady,

        /** Shizuku grants screen sharing for good and plays the PC's input in as real touches. */
        Shizuku
    }

    /** Offer to continue the PC's activity when the phone is unlocked. */
    fun suggestFromPc(context: Context): Boolean = prefs(context).getBoolean(SUGGEST_FROM_PC, true)

    fun setSuggestFromPc(context: Context, value: Boolean) = prefs(context).edit().putBoolean(SUGGEST_FROM_PC, value).apply()

    /** Copies travel between this phone and the PC while connected, not only while one shows the other. */
    fun syncClipboard(context: Context): Boolean = prefs(context).getBoolean(SYNC_CLIPBOARD, true)

    fun setSyncClipboard(context: Context, value: Boolean) = prefs(context).edit().putBoolean(SYNC_CLIPBOARD, value).apply()

    /** This device wakes the PC for the others while it sleeps (a tablet that stays at home). */
    fun wakeRelay(context: Context): Boolean = prefs(context).getBoolean(WAKE_RELAY, false)

    fun setWakeRelay(context: Context, value: Boolean) = prefs(context).edit().putBoolean(WAKE_RELAY, value).apply()

    /** The PC may notice this phone nearby over Bluetooth, to time its suggestions. */
    fun nearby(context: Context): Boolean = prefs(context).getBoolean(NEARBY, false)

    fun setNearby(context: Context, value: Boolean) = prefs(context).edit().putBoolean(NEARBY, value).apply()

    fun mirrorMode(context: Context): MirrorMode =
        prefs(context).getString(MIRROR_MODE, null)?.let { name -> MirrorMode.entries.firstOrNull { it.name == name } }
            ?: MirrorMode.AskEachTime

    fun setMirrorMode(context: Context, mode: MirrorMode) = prefs(context).edit().putString(MIRROR_MODE, mode.name).apply()

    /** In the Shizuku mode: switch Wireless debugging off again once Shizuku runs (it only needs it to start). */
    fun wirelessDebuggingOff(context: Context): Boolean = prefs(context).getBoolean(WIRELESS_DEBUGGING_OFF, true)

    fun setWirelessDebuggingOff(context: Context, value: Boolean) = prefs(context).edit().putBoolean(WIRELESS_DEBUGGING_OFF, value).apply()

    /** Three-finger swipe up sends to the PC, down continues the PC's activity here (needs Shizuku). */
    fun swipes(context: Context): Boolean = prefs(context).getBoolean(SWIPES, false)

    fun setSwipes(context: Context, value: Boolean) = prefs(context).edit().putBoolean(SWIPES, value).apply()

    /** Apps (packages) in which three-finger swipes do nothing, e.g. games that use them. */
    fun swipesOffApps(context: Context): Set<String> = prefs(context).getStringSet(SWIPES_OFF_APPS, null).orEmpty().toSet()

    fun setSwipesOffApps(context: Context, value: Set<String>) = prefs(context).edit().putStringSet(SWIPES_OFF_APPS, value).apply()

    /** This phone's notifications show on the PC (while the phone isn't in use). */
    fun mirrorNotifications(context: Context): Boolean = prefs(context).getBoolean(MIRROR_NOTIFICATIONS, true)

    fun setMirrorNotifications(context: Context, value: Boolean) = prefs(context).edit().putBoolean(MIRROR_NOTIFICATIONS, value).apply()

    /** Apps whose notifications stay on the phone. */
    fun notificationsOffApps(context: Context): Set<String> = prefs(context).getStringSet(NOTIFICATIONS_OFF_APPS, null).orEmpty().toSet()

    fun setNotificationsOffApps(context: Context, value: Set<String>) = prefs(context).edit().putStringSet(NOTIFICATIONS_OFF_APPS, value).apply()

    /** The Shizuku mode was left while Shizuku was stopped: its screen-sharing grant is still to be taken back. */
    internal fun revokePending(context: Context): Boolean = prefs(context).getBoolean(REVOKE_PENDING, false)

    internal fun setRevokePending(context: Context, value: Boolean) = prefs(context).edit().putBoolean(REVOKE_PENDING, value).apply()

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)
}
