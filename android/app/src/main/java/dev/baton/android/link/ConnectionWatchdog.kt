package dev.baton.android.link

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.SystemClock
import android.util.Log

/**
 * Periodic "are we still linked?" alarm.
 *
 * The foreground service is START_STICKY and the socket has its own retry ladder, but neither
 * survives the process being killed outright while the phone is idle - which is exactly when
 * nobody is looking. This alarm restarts the service from outside it.
 *
 * [AlarmManager.setAndAllowWhileIdle] rather than an exact alarm on purpose: it fires through
 * doze, needs no restricted scheduling permission, and a few minutes of imprecision costs nothing
 * for a liveness check.
 */
object ConnectionWatchdog {
    const val ACTION_WATCHDOG = "dev.baton.android.WATCHDOG"

    private const val REQUEST_CODE = 7817
    private const val INTERVAL_MS = 15L * 60L * 1000L

    fun schedule(context: Context) {
        val alarms = context.getSystemService(AlarmManager::class.java) ?: return
        runCatching {
            alarms.setAndAllowWhileIdle(
                AlarmManager.ELAPSED_REALTIME_WAKEUP,
                SystemClock.elapsedRealtime() + INTERVAL_MS,
                pendingIntent(context)
            )
        }.onFailure { Log.w("BatonWatchdog", "Could not schedule the connection watchdog.", it) }
    }

    fun cancel(context: Context) {
        val alarms = context.getSystemService(AlarmManager::class.java) ?: return
        runCatching { alarms.cancel(pendingIntent(context)) }
    }

    private fun pendingIntent(context: Context): PendingIntent {
        val intent = Intent(context, AutoConnectReceiver::class.java).setAction(ACTION_WATCHDOG)
        return PendingIntent.getBroadcast(
            context,
            REQUEST_CODE,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }
}
