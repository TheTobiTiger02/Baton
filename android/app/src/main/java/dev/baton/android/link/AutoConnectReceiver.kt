package dev.baton.android.link

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/** Brings the link back after a reboot, an app update, or a process kill (via the watchdog alarm). */
class AutoConnectReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action
        if (action != Intent.ACTION_BOOT_COMPLETED &&
            action != Intent.ACTION_MY_PACKAGE_REPLACED &&
            action != ConnectionWatchdog.ACTION_WATCHDOG
        ) {
            return
        }

        if (!TrustStore(context).isPaired) {
            ConnectionWatchdog.cancel(context)
            return
        }

        // setAndAllowWhileIdle is one-shot, so the next tick only exists if this one schedules it.
        ConnectionWatchdog.schedule(context)
        if (action == ConnectionWatchdog.ACTION_WATCHDOG && Link.isReady) return

        runCatching { LinkService.start(context) }
            .onFailure { Log.w("BatonBoot", "Automatic reconnect could not start yet.", it) }
    }

    companion object {
        fun enable(context: Context) = ConnectionWatchdog.schedule(context)

        fun disable(context: Context) = ConnectionWatchdog.cancel(context)
    }
}
