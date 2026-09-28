package dev.baton.android.link

import android.annotation.SuppressLint
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings

/**
 * Getting and keeping the background execution the link needs.
 *
 * Two separate gates have to be open on a Galaxy device, and only one of them is visible to
 * [PowerManager.isIgnoringBatteryOptimizations]:
 *
 *  1. The AOSP battery-optimization allowlist, which the standard request dialog covers.
 *  2. One UI's own "Sleeping apps" / "Deep sleeping apps" list, which is invisible to that API and
 *     is the usual reason a Samsung phone silently stops syncing overnight.
 *
 * So the exemption state reported here is necessary but not sufficient, and the Samsung deep link
 * is offered even when the phone claims to be exempt.
 */
object BatteryOptimizationPrompt {

    fun isExempt(context: Context): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return true
        val power = context.getSystemService(PowerManager::class.java) ?: return true
        return power.isIgnoringBatteryOptimizations(context.packageName)
    }

    fun isOneUi(): Boolean = Build.MANUFACTURER.equals("samsung", ignoreCase = true)

    /**
     * Fires the system exemption dialog, falling back to the allowlist settings screen when the
     * direct request is unavailable. Returns false when neither could be shown.
     */
    @SuppressLint("BatteryLife")
    fun requestExemption(context: Context): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return true

        val request = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
            .setData(Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (start(context, request)) return true

        val settings = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return start(context, settings)
    }

    /**
     * Opens One UI's sleeping-apps list. The activity is not public API and has moved between One
     * UI releases, so every known location is tried before falling back to app info.
     */
    fun openOneUiSleepingApps(context: Context): Boolean {
        if (!isOneUi()) return false

        val candidates = listOf(
            ComponentName(
                "com.samsung.android.lool",
                "com.samsung.android.sm.ui.battery.BatteryActivity"
            ),
            ComponentName(
                "com.samsung.android.lool",
                "com.samsung.android.sm.battery.ui.BatteryActivity"
            ),
            ComponentName(
                "com.samsung.android.sm",
                "com.samsung.android.sm.ui.battery.BatteryActivity"
            )
        )

        for (component in candidates) {
            val intent = Intent().setComponent(component).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            if (start(context, intent)) return true
        }

        val appInfo = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
            .setData(Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return start(context, appInfo)
    }

    private fun start(context: Context, intent: Intent): Boolean =
        runCatching { context.startActivity(intent) }.isSuccess
}
