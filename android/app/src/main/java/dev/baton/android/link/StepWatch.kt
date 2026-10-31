package dev.baton.android.link

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.hardware.TriggerEvent
import android.hardware.TriggerEventListener
import android.os.SystemClock
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.MotionPayload

/**
 * Tells the PC when this phone starts walking: the Bluetooth signal alone can't tell standing up
 * from sitting, since the first steps away read the same as the desk. The PC decides what it
 * means: walking while nobody touches the PC is leaving it.
 *
 * Two low-power sensors: the step detector, which only reaches the app while the phone is awake
 * (screen on), and the significant-motion trigger, which wakes it (in a pocket, screen off) when
 * the user starts to walk somewhere. Either one tells the PC.
 */
object StepWatch {
    /** Steps that make a walk, not shifting in the chair. */
    private const val STEPS = 3
    private const val WITHIN_MS = 6_000L

    /** Once told, the PC needn't hear it again for a while: it only matters at the start of a walk. */
    private const val QUIET_MS = 20_000L

    private var manager: SensorManager? = null
    private var motion: Sensor? = null
    private val steps = ArrayDeque<Long>()
    private var toldAt = 0L

    private val listener = object : SensorEventListener {
        override fun onSensorChanged(event: SensorEvent) = onStep(SystemClock.elapsedRealtime())
        override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit
    }

    /** One-shot: it fires once and must be asked again. */
    private val trigger = object : TriggerEventListener() {
        override fun onTrigger(event: TriggerEvent) {
            tell(SystemClock.elapsedRealtime(), "motion.significant")
            motion?.let { manager?.requestTriggerSensor(this, it) }
        }
    }

    fun hasPermission(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.ACTIVITY_RECOGNITION) == PackageManager.PERMISSION_GRANTED

    /** Starts listening, again after a permission was granted; the step detector needs it, the motion trigger doesn't. */
    fun start(context: Context) {
        stop()
        val sensors = context.getSystemService(SensorManager::class.java) ?: return
        manager = sensors
        if (hasPermission(context)) {
            sensors.getDefaultSensor(Sensor.TYPE_STEP_DETECTOR)?.let { sensors.registerListener(listener, it, SensorManager.SENSOR_DELAY_NORMAL) }
        }
        motion = sensors.getDefaultSensor(Sensor.TYPE_SIGNIFICANT_MOTION)?.also { sensors.requestTriggerSensor(trigger, it) }
    }

    fun stop() {
        manager?.unregisterListener(listener)
        motion?.let { manager?.cancelTriggerSensor(trigger, it) }
        manager = null
        motion = null
        synchronized(steps) { steps.clear() }
    }

    private fun onStep(now: Long) {
        val walking = synchronized(steps) {
            steps.addLast(now)
            while (steps.isNotEmpty() && now - steps.first() > WITHIN_MS) steps.removeFirst()
            steps.size >= STEPS
        }
        if (walking) tell(now, "motion.walking")
    }

    private fun tell(now: Long, what: String) {
        if (now - toldAt <= QUIET_MS || !Link.isReady) return
        if (Link.send(MessageTypes.DEVICE_MOTION, MotionPayload(walking = true))) {
            toldAt = now
            LinkDiagnostics.record(what)
        }
    }
}
