package dev.baton.android.link

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.SystemClock
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.MotionPayload

/**
 * Tells the PC when this phone starts walking, from the step sensor (a low-power chip that counts
 * steps anyway): the Bluetooth signal alone can't tell standing up from sitting, since the first
 * steps away read the same as the desk. The PC decides what it means: walking while nobody
 * touches the PC is leaving it.
 */
object StepWatch {
    /** Steps that make a walk, not shifting in the chair. */
    private const val STEPS = 3
    private const val WITHIN_MS = 6_000L

    /** Once told, the PC needn't hear it again for a while: it only matters at the start of a walk. */
    private const val QUIET_MS = 20_000L

    private var manager: SensorManager? = null
    private val steps = ArrayDeque<Long>()
    private var toldAt = 0L

    private val listener = object : SensorEventListener {
        override fun onSensorChanged(event: SensorEvent) = onStep(SystemClock.elapsedRealtime())
        override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit
    }

    fun hasPermission(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.ACTIVITY_RECOGNITION) == PackageManager.PERMISSION_GRANTED

    /** Starts listening; does nothing without the permission or a step sensor. */
    fun start(context: Context) {
        if (manager != null || !hasPermission(context)) return
        val sensors = context.getSystemService(SensorManager::class.java) ?: return
        // The wake-up kind reaches the app with the screen off, as a phone in a pocket has it.
        val sensor = sensors.getDefaultSensor(Sensor.TYPE_STEP_DETECTOR, true)
            ?: sensors.getDefaultSensor(Sensor.TYPE_STEP_DETECTOR) ?: return
        if (sensors.registerListener(listener, sensor, SensorManager.SENSOR_DELAY_NORMAL)) manager = sensors
    }

    fun stop() {
        manager?.unregisterListener(listener)
        manager = null
        synchronized(steps) { steps.clear() }
    }

    private fun onStep(now: Long) {
        val walking = synchronized(steps) {
            steps.addLast(now)
            while (steps.isNotEmpty() && now - steps.first() > WITHIN_MS) steps.removeFirst()
            steps.size >= STEPS && now - toldAt > QUIET_MS
        }
        if (walking && Link.isReady && Link.send(MessageTypes.DEVICE_MOTION, MotionPayload(walking = true))) {
            toldAt = now
            LinkDiagnostics.record("motion.walking")
        }
    }
}
