package dev.baton.android.apps

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Bitmap
import android.graphics.Canvas
import android.util.Base64
import android.util.LruCache
import dev.baton.android.link.Link
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.AppCatalogPayload
import dev.baton.android.protocol.AppPreference
import dev.baton.android.protocol.AppPreferenceRemovePayload
import dev.baton.android.protocol.ChoiceKeys
import dev.baton.android.protocol.ChoiceKinds
import dev.baton.android.protocol.HandoffChoice
import dev.baton.android.protocol.HandoffOptionsPayload
import dev.baton.android.protocol.HandoffOptionsRequest
import dev.baton.android.protocol.InstalledApp
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.Platforms
import dev.baton.android.protocol.Wire
import java.io.ByteArrayOutputStream
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withTimeoutOrNull

/** Every app on this phone a person can open, as the launcher lists them. */
object AppCatalog {
    private val icons = LruCache<String, Bitmap>(200)
    private var receiver: BroadcastReceiver? = null

    /** Launchable apps except Baton itself, by name. */
    fun apps(context: Context): List<InstalledApp> {
        val packages = context.packageManager
        val launcher = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        return packages.queryIntentActivities(launcher, 0)
            .map { InstalledApp(it.activityInfo.packageName, it.loadLabel(packages).toString()) }
            .filter { it.id != context.packageName }
            .distinctBy { it.id }
            .sortedBy { it.name.lowercase() }
    }

    fun icon(context: Context, packageName: String, size: Int = 96): Bitmap? = icons.get(packageName) ?: runCatching {
        val drawable = context.packageManager.getApplicationIcon(packageName)
        Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888).also {
            drawable.setBounds(0, 0, size, size)
            drawable.draw(Canvas(it))
            icons.put(packageName, it)
        }
    }.getOrNull()

    /** An app as an activity: it continues by opening its PC twin, or by showing this screen. */
    fun activity(context: Context, app: InstalledApp): Activity = Activity(
        id = "app:${app.id}",
        deviceId = Link.deviceId,
        kind = ActivityKind.WindowStream,
        title = app.name,
        app = ActivityApp(app.name, app.id),
        updatedAt = Wire.time(System.currentTimeMillis()),
        artworkJpegBase64 = icon(context, app.id, 128)?.let { bitmap ->
            val jpeg = Bitmap.createBitmap(bitmap.width, bitmap.height, Bitmap.Config.ARGB_8888)
            Canvas(jpeg).apply {
                drawColor(android.graphics.Color.WHITE)
                drawBitmap(bitmap, 0f, 0f, null)
            }
            ByteArrayOutputStream().use {
                jpeg.compress(Bitmap.CompressFormat.JPEG, 85, it)
                Base64.encodeToString(it.toByteArray(), Base64.NO_WRAP)
            }
        }
    )

    /** Tells the PC which apps this phone has, so it can match them; again whenever that changes. */
    fun publish(context: Context) {
        Link.send(MessageTypes.APP_CATALOG, AppCatalogPayload(apps(context)))
    }

    fun watch(context: Context) {
        if (receiver != null) return
        val app = context.applicationContext
        receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = publish(app)
        }.also {
            app.registerReceiver(it, IntentFilter().apply {
                addAction(Intent.ACTION_PACKAGE_ADDED)
                addAction(Intent.ACTION_PACKAGE_REMOVED)
                addDataScheme("package")
            })
        }
    }
}

/**
 * Which app something continues in: the choices remembered on the PC (a copy kept here so asking
 * never costs a round trip) and, when there is none, the options the PC offers.
 */
object Choices {
    private val preferencesFlow = MutableStateFlow<Map<String, AppPreference>>(emptyMap())
    private val waiting = ConcurrentHashMap<String, CompletableDeferred<HandoffOptionsPayload>>()

    val preferences: StateFlow<Map<String, AppPreference>> = preferencesFlow.asStateFlow()

    fun platformOf(deviceId: String) = if (deviceId == Link.hostId) Platforms.WINDOWS else Platforms.ANDROID

    /** This phone's package manager, to skip remembered apps it doesn't have; set while Baton runs. */
    @Volatile var packages: android.content.pm.PackageManager? = null

    /**
     * The choice remembered for this app and direction: the one for [targetDeviceId] first (each
     * phone has its own YouTube build), then the shared one. An app this phone doesn't have (RVX
     * picked on another phone) is no choice here.
     */
    fun remembered(activity: Activity, sourceDeviceId: String, targetDeviceId: String): HandoffChoice? {
        val key = ChoiceKeys.of(platformOf(sourceDeviceId), activity, platformOf(targetDeviceId))
        val choice = (preferencesFlow.value["$key@$targetDeviceId"] ?: preferencesFlow.value[key])?.choice ?: return null
        val app = choice.appId
        val missing = targetDeviceId == Link.deviceId && choice.kind == ChoiceKinds.APP && app != null &&
            packages?.let { pm -> runCatching { pm.getPackageInfo(app, 0) }.isFailure } == true
        return if (missing) null else choice
    }

    /** Asks the PC how [activity] can continue on [targetDeviceId]. Null when it doesn't answer. */
    suspend fun options(activity: Activity, targetDeviceId: String): HandoffOptionsPayload? {
        val requestId = Wire.newId()
        val reply = CompletableDeferred<HandoffOptionsPayload>()
        waiting[requestId] = reply
        return try {
            if (!Link.send(MessageTypes.HANDOFF_OPTIONS, HandoffOptionsRequest(requestId, activity, targetDeviceId))) return null
            withTimeoutOrNull(3_000) { reply.await() }
        } finally {
            waiting.remove(requestId)
        }
    }

    fun onOptions(payload: HandoffOptionsPayload) {
        waiting[payload.requestId]?.complete(payload)
    }

    fun onPreferences(list: List<AppPreference>) {
        preferencesFlow.value = list.associateBy { it.key }
    }

    fun forget(key: String) {
        preferencesFlow.value = preferencesFlow.value - key
        Link.send(MessageTypes.APP_PREFERENCE_REMOVE, AppPreferenceRemovePayload(key))
    }
}
