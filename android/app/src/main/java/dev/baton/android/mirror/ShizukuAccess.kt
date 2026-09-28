package dev.baton.android.mirror

import android.content.ComponentName
import android.content.Context
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.IBinder
import android.util.Log
import dev.baton.android.BatonSettings
import dev.baton.android.handoff.BatonAccessibilityService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import rikka.shizuku.Shizuku

/**
 * The Shizuku screen-sharing mode. Shizuku lets Baton act as the shell user (like adb shell) to
 * grant itself screen sharing for good (the PROJECT_MEDIA app-op, so Android's consent returns at
 * once), and to play the PC's input in as a real touchscreen through [ShizukuShell].
 */
object ShizukuAccess {
    private const val TAG = "BatonShizuku"
    private const val SHIZUKU_PACKAGE = "moe.shizuku.privileged.api"
    private const val SHIZUKU_BOOT_RECEIVER = "moe.shizuku.manager.receiver.BootCompleteReceiver"
    private const val REQUEST_CODE = 7

    enum class State { NotInstalled, NotRunning, NotAllowed, Ready }

    private val stateFlow = MutableStateFlow(State.NotRunning)
    val state: StateFlow<State> = stateFlow.asStateFlow()

    /** The helper process, while bound. */
    @Volatile var shell: IShizukuShell? = null
        private set

    private var appContext: Context? = null
    private var bound = false
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val serviceArgs by lazy {
        Shizuku.UserServiceArgs(ComponentName(appContext!!.packageName, ShizukuShell::class.java.name))
            .daemon(false)
            .processNameSuffix("shell")
            .version(1)
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            shell = binder?.takeIf { it.pingBinder() }?.let { IShizukuShell.Stub.asInterface(it) }
            PhoneMirror.inputChanged()
            scope.launch { settle() }
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            shell = null
            bound = false
            PhoneMirror.inputChanged()
        }
    }

    /** Starts following Shizuku; binds the helper whenever the Shizuku mode is on and allowed. */
    fun init(context: Context) {
        if (appContext != null) return
        appContext = context.applicationContext
        Shizuku.addBinderReceivedListenerSticky { refresh() }
        Shizuku.addBinderDeadListener {
            shell = null
            bound = false
            refresh()
        }
        Shizuku.addRequestPermissionResultListener { requestCode, _ -> if (requestCode == REQUEST_CODE) refresh() }
        refresh()
    }

    /** Re-reads Shizuku's state and binds or unbinds the helper to match the chosen mode. */
    @Synchronized
    fun refresh() {
        val context = appContext ?: return
        val next = when {
            !installed(context) -> State.NotInstalled
            !runCatching { Shizuku.pingBinder() && !Shizuku.isPreV11() }.getOrDefault(false) -> State.NotRunning
            Shizuku.checkSelfPermission() != PackageManager.PERMISSION_GRANTED -> State.NotAllowed
            else -> State.Ready
        }
        stateFlow.value = next
        val wanted = next == State.Ready &&
            (BatonSettings.mirrorMode(context) == BatonSettings.MirrorMode.Shizuku || BatonSettings.revokePending(context))
        if (wanted && !bound) {
            bound = runCatching { Shizuku.bindUserService(serviceArgs, connection) }
                .onFailure { Log.w(TAG, "Could not start the Shizuku helper", it) }
                .isSuccess
        } else if (!wanted && bound) {
            runCatching { Shizuku.unbindUserService(serviceArgs, connection, true) }
            bound = false
            shell = null
            PhoneMirror.inputChanged()
        }
    }

    /** Shows Shizuku's own prompt to let Baton use it. */
    fun requestPermission() {
        runCatching { Shizuku.requestPermission(REQUEST_CODE) }
    }

    fun openShizuku(context: Context) {
        context.packageManager.getLaunchIntentForPackage(SHIZUKU_PACKAGE)?.let {
            context.startActivity(it.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }

    /**
     * Leaving the Shizuku mode takes the grant back (while Shizuku can still do it), so "ask each
     * time" really asks. Entering it grants once the helper is up.
     */
    fun modeChanged(previous: BatonSettings.MirrorMode, next: BatonSettings.MirrorMode) {
        val context = appContext ?: return
        scope.launch {
            if (next == BatonSettings.MirrorMode.Shizuku) {
                BatonSettings.setRevokePending(context, false)
            } else if (previous == BatonSettings.MirrorMode.Shizuku) {
                // Shizuku stopped: the grant is taken back the next time it runs.
                if (shell != null) grantScreenSharing(false) else BatonSettings.setRevokePending(context, true)
            }
            // Only now may the helper go: the revoke above ran in it.
            refresh()
        }
    }

    /** The "turn off Wireless debugging" switch changed: applies it now when Shizuku runs. */
    fun wirelessDebuggingSettingChanged() {
        scope.launch { settle() }
    }

    /**
     * Whether Shizuku starts again by itself after a restart (Shizuku 13.6+, Android 13+, no root):
     * it needs WRITE_SECURE_SETTINGS ([hasRunBefore]) and its "Start on boot" switch on (its boot
     * receiver, on unless turned off in Shizuku). Shizuku also needs its own Wireless debugging
     * pairing, which cannot be seen from here.
     */
    fun startsOnBoot(context: Context): Boolean {
        val permission = hasRunBefore(context)
        val receiver = runCatching { context.packageManager.getComponentEnabledSetting(ComponentName(SHIZUKU_PACKAGE, SHIZUKU_BOOT_RECEIVER)) }
            .getOrNull()
        return permission && (receiver == PackageManager.COMPONENT_ENABLED_STATE_DEFAULT || receiver == PackageManager.COMPONENT_ENABLED_STATE_ENABLED)
    }

    /**
     * Shizuku has run on this phone before: its server grants the Shizuku app WRITE_SECURE_SETTINGS
     * when it first starts, so it has been set up, and starting it again is a tap on Start.
     */
    fun hasRunBefore(context: Context): Boolean =
        context.packageManager.checkPermission(android.Manifest.permission.WRITE_SECURE_SETTINGS, SHIZUKU_PACKAGE) ==
            PackageManager.PERMISSION_GRANTED

    /**
     * Blocking, in the helper: brings the phone in line with the chosen mode. In the Shizuku mode:
     * the screen-sharing grant, and Wireless debugging off again (Shizuku keeps running without it;
     * it only needs it to start). Otherwise a grant still to be taken back is taken back, and the
     * helper goes.
     */
    private fun settle() {
        val context = appContext ?: return
        val helper = shell ?: return
        if (BatonSettings.mirrorMode(context) == BatonSettings.MirrorMode.Shizuku) {
            grantScreenSharing(true)
            if (BatonSettings.wirelessDebuggingOff(context)) run(helper, "settings", "put", "global", "adb_wifi_enabled", "0")
        } else if (BatonSettings.revokePending(context)) {
            grantScreenSharing(false)
            BatonSettings.setRevokePending(context, false)
            refresh()
        }
    }

    private fun run(helper: IShizukuShell, vararg command: String) {
        runCatching { helper.exec(arrayOf(*command)) }.onFailure { Log.w(TAG, "Could not run ${command.first()}", it) }
    }

    /** Turns on Baton's accessibility service (keys and text from the PC) without the Settings screens. */
    fun enableAccessibility(context: Context) {
        val helper = shell ?: return
        val component = ComponentName(context, BatonAccessibilityService::class.java).flattenToString()
        scope.launch {
            runCatching {
                val current = helper.exec(arrayOf("settings", "get", "secure", "enabled_accessibility_services")).trim()
                    .takeUnless { it.isBlank() || it == "null" }
                if (current?.split(':')?.contains(component) != true) {
                    val services = listOfNotNull(current, component).joinToString(":")
                    helper.exec(arrayOf("settings", "put", "secure", "enabled_accessibility_services", services))
                    helper.exec(arrayOf("settings", "put", "secure", "accessibility_enabled", "1"))
                }
            }.onFailure { Log.w(TAG, "Could not turn on accessibility", it) }
        }
    }

    /** Blocking: runs in the helper process. */
    private fun grantScreenSharing(allow: Boolean) {
        val helper = shell ?: return
        val packageName = appContext?.packageName ?: return
        runCatching { helper.exec(arrayOf("appops", "set", packageName, "PROJECT_MEDIA", if (allow) "allow" else "default")) }
            .onFailure { Log.w(TAG, "Could not change screen-sharing permission", it) }
    }

    private fun installed(context: Context): Boolean =
        runCatching { context.packageManager.getPackageInfo(SHIZUKU_PACKAGE, 0) }.isSuccess
}
