package dev.baton.android.link

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.PowerManager
import android.os.SystemClock
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.MotionPayload

/**
 * Tells the PC when this phone starts walking: the Bluetooth signal alone can't tell standing up
 * from sitting, since the first steps away read the same as the desk. The PC decides what it
 * means: walking while nobody touches the PC is leaving it.
 *
 * The step detector only reaches the app while the phone is awake: with the screen off it sleeps
 * and the steps are dropped (on the S21 the sensor can't wake it, and its significant-motion
 * trigger waits for a longer walk than leaving a desk). So while this phone is near the PC and the
 * PC is in use, which is when getting up means leaving it, the phone stays awake enough to count
 * steps (a partial wake lock, about 1% battery an hour). Otherwise it sleeps as usual.
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
    private var awake: PowerManager.WakeLock? = null
    @Volatile private var near = false
    @Volatile private var pcInUse = false

    private val listener = object : SensorEventListener {
        override fun onSensorChanged(event: SensorEvent) = onStep(SystemClock.elapsedRealtime())
        override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit
    }

    fun hasPermission(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.ACTIVITY_RECOGNITION) == PackageManager.PERMISSION_GRANTED

    /** Starts listening, again after a permission was granted; does nothing without it or a step sensor. */
    fun start(context: Context) {
        stop()
        if (!hasPermission(context)) return
        val sensors = context.getSystemService(SensorManager::class.java) ?: return
        val sensor = sensors.getDefaultSensor(Sensor.TYPE_STEP_DETECTOR) ?: return
        if (!sensors.registerListener(listener, sensor, SensorManager.SENSOR_DELAY_NORMAL)) return
        manager = sensors
        awake = context.getSystemService(PowerManager::class.java)
            ?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "baton:steps")?.apply { setReferenceCounted(false) }
        arm()
    }

    fun stop() {
        manager?.unregisterListener(listener)
        manager = null
        awake?.release()
        awake = null
        synchronized(steps) { steps.clear() }
    }

    /** The PC said this phone is near it (or not). */
    fun setNear(value: Boolean) {
        near = value
        arm()
    }

    /** Whether the PC is in use (someone at its keyboard or mouse lately). */
    fun setPcInUse(value: Boolean) {
        if (pcInUse == value) return
        pcInUse = value
        arm()
    }

    /** Awake to count steps only while getting up would mean leaving the PC. */
    @Synchronized
    private fun arm() {
        val lock = awake ?: return
        val armed = near && pcInUse
        if (armed && !lock.isHeld) {
            lock.acquire()
            LinkDiagnostics.record("motion.armed")
        } else if (!armed && lock.isHeld) {
            lock.release()
            LinkDiagnostics.record("motion.disarmed")
        }
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
