package dev.baton.android

import android.content.Context

/** This phone's own switches. */
object BatonSettings {
    private const val FILE = "baton_settings"
    private const val SUGGEST_FROM_PC = "suggest_from_pc"
    private const val MIRROR_MODE = "mirror_mode"
    private const val WIRELESS_DEBUGGING_OFF = "wireless_debugging_off"
    private const val REVOKE_PENDING = "revoke_pending"
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

    fun mirrorMode(context: Context): MirrorMode =
        prefs(context).getString(MIRROR_MODE, null)?.let { name -> MirrorMode.entries.firstOrNull { it.name == name } }
            ?: MirrorMode.AskEachTime

    fun setMirrorMode(context: Context, mode: MirrorMode) = prefs(context).edit().putString(MIRROR_MODE, mode.name).apply()

    /** In the Shizuku mode: switch Wireless debugging off again once Shizuku runs (it only needs it to start). */
    fun wirelessDebuggingOff(context: Context): Boolean = prefs(context).getBoolean(WIRELESS_DEBUGGING_OFF, true)

    fun setWirelessDebuggingOff(context: Context, value: Boolean) = prefs(context).edit().putBoolean(WIRELESS_DEBUGGING_OFF, value).apply()

    /** The Shizuku mode was left while Shizuku was stopped: its screen-sharing grant is still to be taken back. */
    internal fun revokePending(context: Context): Boolean = prefs(context).getBoolean(REVOKE_PENDING, false)

    internal fun setRevokePending(context: Context, value: Boolean) = prefs(context).edit().putBoolean(REVOKE_PENDING, value).apply()

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)
}
