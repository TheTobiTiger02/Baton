package dev.baton.android.remote

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import dev.baton.android.R
import dev.baton.android.ui.HandoffActionReceiver

/**
 * "Ring" from the PC, to find this phone: the alarm sound at full alarm volume, so it is heard
 * even on silent, with vibration, until stopped from its notification or after the PC's time is
 * up. The alarm volume the user had comes back when it stops.
 */
object PhoneRinger {
    private const val CHANNEL = "ring"
    private const val NOTIFICATION_ID = 5
    private const val MIN_SECONDS = 5
    private const val MAX_SECONDS = 120

    private val main = Handler(Looper.getMainLooper())
    private var ringtone: Ringtone? = null
    private var restoreVolume: Int? = null
    private var context: Context? = null
    private val timeUp = Runnable { context?.let(::stop) }

    fun ring(context: Context, seconds: Int) {
        main.post {
            val app = context.applicationContext
            this.context = app
            stopSound(app)
            val audio = app.getSystemService(AudioManager::class.java)
            if (restoreVolume == null) restoreVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM)
            // Do Not Disturb can refuse the change; the alarm then rings at the volume it has.
            runCatching { audio.setStreamVolume(AudioManager.STREAM_ALARM, audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0) }
            val uri = RingtoneManager.getActualDefaultRingtoneUri(app, RingtoneManager.TYPE_ALARM)
                ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
            ringtone = RingtoneManager.getRingtone(app, uri)?.apply {
                audioAttributes = AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                    .build()
                isLooping = true
                play()
            }
            runCatching {
                @Suppress("DEPRECATION")
                app.getSystemService(Vibrator::class.java)?.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 800, 600), 0),
                    AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM).build())
            }
            notify(app)
            main.removeCallbacks(timeUp)
            main.postDelayed(timeUp, seconds.coerceIn(MIN_SECONDS, MAX_SECONDS) * 1_000L)
        }
    }

    fun stop(context: Context) {
        main.post {
            val app = context.applicationContext
            main.removeCallbacks(timeUp)
            stopSound(app)
            restoreVolume?.let { volume ->
                runCatching { app.getSystemService(AudioManager::class.java).setStreamVolume(AudioManager.STREAM_ALARM, volume, 0) }
            }
            restoreVolume = null
            app.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION_ID)
        }
    }

    private fun stopSound(context: Context) {
        runCatching { ringtone?.stop() }
        ringtone = null
        runCatching { context.getSystemService(Vibrator::class.java)?.cancel() }
    }

    private fun notify(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Ringing from your PC", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Shown while your PC makes this phone ring, so you can stop it."
                setSound(null, null)
            })
        }
        val stop = PendingIntent.getBroadcast(context, NOTIFICATION_ID, HandoffActionReceiver.stopRinging(context),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_baton)
            .setContentTitle("Your PC is ringing this phone")
            .setContentText("Tap to stop.")
            .setCategory(Notification.CATEGORY_ALARM)
            .setOngoing(true)
            .setContentIntent(stop)
            .setDeleteIntent(stop)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
        runCatching { manager.notify(NOTIFICATION_ID, notification) }
    }
}
