package dev.baton.android.apps

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.util.Log
import dev.baton.android.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import okhttp3.Request

/**
 * Updates from the GitHub releases of Baton's repository, the same place the PC app updates from:
 * a newer release's APK is downloaded and handed to Android's installer, which asks the user.
 */
object AppUpdates {
    const val REPOSITORY = "TheTobiTiger02/Baton"
    private const val TAG = "BatonUpdates"

    data class Release(val version: String, val apkUrl: String)

    private val client = OkHttpClient()
    private val availableFlow = MutableStateFlow<Release?>(null)

    /** A newer release, once a check found one. */
    val available: StateFlow<Release?> = availableFlow.asStateFlow()

    val currentVersion: String get() = BuildConfig.VERSION_NAME

    /** Asks GitHub for the latest release. Null when there is none newer, or no answer. */
    suspend fun check(): Release? = withContext(Dispatchers.IO) {
        runCatching {
            val request = Request.Builder()
                .url("https://api.github.com/repos/$REPOSITORY/releases/latest")
                .header("Accept", "application/vnd.github+json")
                .build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) return@use null
                val release = Json.parseToJsonElement(response.body?.string().orEmpty()).jsonObject
                val version = release["tag_name"]?.jsonPrimitive?.content?.removePrefix("v") ?: return@use null
                val apk = release["assets"]?.jsonArray?.map { it.jsonObject }
                    ?.firstOrNull { it["name"]?.jsonPrimitive?.content?.endsWith(".apk") == true }
                    ?.get("browser_download_url")?.jsonPrimitive?.content ?: return@use null
                Release(version, apk).takeIf { isNewer(version, currentVersion) }
            }
        }.onFailure { Log.w(TAG, "Update check failed", it) }.getOrNull().also { availableFlow.value = it }
    }

    /** "1.10.0" is newer than "1.9.3"; anything that isn't a version never is. */
    fun isNewer(candidate: String, current: String): Boolean {
        val a = candidate.split('.').map { it.toIntOrNull() ?: return false }
        val b = current.split('.').map { it.toIntOrNull() ?: 0 }
        for (index in 0 until maxOf(a.size, b.size)) {
            val difference = a.getOrElse(index) { 0 } - b.getOrElse(index) { 0 }
            if (difference != 0) return difference > 0
        }
        return false
    }

    /** Downloads the release into an install session; Android then asks to install it. */
    suspend fun install(context: Context, release: Release): Boolean = withContext(Dispatchers.IO) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val sessionId = installer.createSession(params)
        runCatching {
            installer.openSession(sessionId).use { session ->
                client.newCall(Request.Builder().url(release.apkUrl).build()).execute().use { response ->
                    check(response.isSuccessful) { "Download failed: ${response.code}" }
                    session.openWrite("Baton.apk", 0, response.body!!.contentLength()).use { output ->
                        response.body!!.byteStream().copyTo(output)
                        session.fsync(output)
                    }
                }
                val done = PendingIntent.getBroadcast(context, sessionId, Intent(context, UpdateInstallReceiver::class.java),
                    PendingIntent.FLAG_MUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
                session.commit(done.intentSender)
            }
            true
        }.onFailure {
            Log.w(TAG, "Update install failed", it)
            runCatching { installer.abandonSession(sessionId) }
        }.getOrDefault(false)
    }
}

/** Android's answer to an install session: asks the user to confirm, as the installer requires. */
class UpdateInstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE) != PackageInstaller.STATUS_PENDING_USER_ACTION) return
        val confirm = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
        } else {
            @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_INTENT)
        } ?: return
        context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
