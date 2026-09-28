package dev.baton.android.handoff

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.util.Base64
import android.view.inputmethod.InputMethodManager
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityApp
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.Wire
import java.io.ByteArrayOutputStream

/**
 * The app in front on this phone, as something the PC can continue by showing this screen: the
 * fallback for apps that have nothing to reopen elsewhere (a game, a chat, a banking app).
 */
object CurrentApp {
    private val icons = mutableMapOf<String, String?>()
    private var since = System.currentTimeMillis()
    private var last: String? = null

    fun activity(context: Context, deviceId: String): Activity? {
        val foreground = BrowserPages.foregroundPackage ?: return null
        // Opening Baton or pulling down the shade to send the app doesn't make it stop being the app.
        val packageName = if (foreground == context.packageName || foreground in SYSTEM) last ?: return null else foreground
        if (packageName != last) {
            last = packageName
            since = System.currentTimeMillis()
        }
        if (!isApp(context, packageName)) return null
        val name = MediaSessions.appName(context, packageName)
        return Activity(
            id = "app:$packageName",
            deviceId = deviceId,
            kind = ActivityKind.WindowStream,
            title = name,
            app = ActivityApp(name, packageName),
            updatedAt = Wire.time(since),
            subtitle = "Open on this phone",
            artworkJpegBase64 = icons.getOrPut(packageName) { icon(context, packageName) }
        )
    }

    /** A real app a person uses: launchable, and not the home screen, a keyboard, Baton or system UI. */
    private fun isApp(context: Context, packageName: String): Boolean {
        val packages = context.packageManager
        if (packageName == context.packageName || packageName in SYSTEM) return false
        if (packages.getLaunchIntentForPackage(packageName) == null) return false
        // The launcher in use only: Settings also declares a (fallback) home screen.
        val home = packages.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), PackageManager.MATCH_DEFAULT_ONLY)
        if (home?.activityInfo?.packageName == packageName) return false
        val keyboards = context.getSystemService(InputMethodManager::class.java).enabledInputMethodList
        return keyboards.none { it.packageName == packageName }
    }

    private fun icon(context: Context, packageName: String): String? = runCatching {
        val drawable = context.packageManager.getApplicationIcon(packageName)
        val bitmap = Bitmap.createBitmap(128, 128, Bitmap.Config.ARGB_8888)
        Canvas(bitmap).drawColor(android.graphics.Color.WHITE)
        drawable.setBounds(0, 0, 128, 128)
        drawable.draw(Canvas(bitmap))
        val out = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.JPEG, 85, out)
        Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
    }.getOrNull()

    private val SYSTEM = setOf("com.android.systemui", "android", "com.google.android.permissioncontroller", "com.android.settings.intelligence")
}
