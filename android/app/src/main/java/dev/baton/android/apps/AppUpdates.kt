package dev.baton.android.apps

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.ConnectivityManager
import android.os.Build
import android.util.Log
import dev.baton.android.BuildConfig
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import okhttp3.Request

/**
 * Updates from the GitHub releases of Baton's repository, the same place the PC app updates from.
 * A newer release's APK is downloaded ahead (on Wi-Fi) or on the tap, with its progress shown,
 * then handed to Android's installer, which asks the user.
 */
object AppUpdates {
    const val REPOSITORY = "TheTobiTiger02/Baton"
    private const val TAG = "BatonUpdates"

    data class Release(val version: String, val apkUrl: String)

    /** Where an update stands, for the banner and Settings → About. */
    sealed interface State {
        data object Idle : State
        data object Checking : State
        data object UpToDate : State
        data class Available(val release: Release) : State
        data class Downloading(val release: Release, val percent: Int) : State
        data class Ready(val release: Release, val apk: File) : State
        data class Failed(val release: Release?, val message: String) : State
    }

    private val client = OkHttpClient()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val stateFlow = MutableStateFlow<State>(State.Idle)
    private var download: Job? = null

    val state: StateFlow<State> = stateFlow.asStateFlow()

    /** The newer release, whatever stage its download is at. */
    val release: Release?
        get() = when (val current = stateFlow.value) {
            is State.Available -> current.release
            is State.Downloading -> current.release
            is State.Ready -> current.release
            is State.Failed -> current.release
            else -> null
        }

    val currentVersion: String get() = BuildConfig.VERSION_NAME

    /**
     * Asks GitHub for the latest release. A newer one on an unmetered network starts downloading
     * right away, so tapping Update opens the installer without waiting.
     */
    suspend fun check(context: Context) {
        // A development build is signed with another key: Android would refuse a release over it.
        if (BuildConfig.DEBUG) return
        if (stateFlow.value is State.Downloading || stateFlow.value is State.Ready) return
        stateFlow.value = State.Checking
        val release = withContext(Dispatchers.IO) { latest() }
        stateFlow.value = if (release == null) State.UpToDate else State.Available(release)
        val metered = context.getSystemService(ConnectivityManager::class.java)?.isActiveNetworkMetered ?: true
        if (release != null && !metered) startDownload(context, release, install = false)
    }

    /** The Update button: install what is downloaded, or download it now (showing progress) and then install. */
    fun update(context: Context) {
        when (val current = stateFlow.value) {
            is State.Ready -> scope.launch { install(context.applicationContext, current.release, current.apk) }
            is State.Downloading -> installWhenReady = true
            else -> release?.let { startDownload(context, it, install = true) }
        }
    }

    @Volatile private var installWhenReady = false

    private fun startDownload(context: Context, release: Release, install: Boolean) {
        installWhenReady = installWhenReady || install
        if (download?.isActive == true) return
        val app = context.applicationContext
        download = scope.launch {
            val apk = File(app.cacheDir, "updates/Baton-${release.version}.apk")
            try {
                if (!apk.exists()) fetch(release, apk)
                stateFlow.value = State.Ready(release, apk)
                if (installWhenReady) install(app, release, apk)
            } catch (e: Exception) {
                Log.w(TAG, "Update download failed", e)
                stateFlow.value = State.Failed(release, "The update couldn't be downloaded. Try again later.")
            }
        }
    }

    private fun fetch(release: Release, apk: File) {
        stateFlow.value = State.Downloading(release, 0)
        apk.parentFile?.apply { mkdirs(); listFiles()?.forEach { if (it != apk) it.delete() } }
        val partial = File(apk.path + ".part")
        client.newCall(Request.Builder().url(release.apkUrl).build()).execute().use { response ->
            check(response.isSuccessful) { "Download failed: ${response.code}" }
            val body = response.body!!
            val total = body.contentLength()
            body.byteStream().use { input ->
                partial.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var copied = 0L
                    var shown = -1
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        copied += read
                        val percent = if (total > 0) (copied * 100 / total).toInt() else 0
                        if (percent != shown) {
                            shown = percent
                            stateFlow.value = State.Downloading(release, percent)
                        }
                    }
                }
            }
        }
        check(partial.renameTo(apk)) { "Couldn't keep the download." }
    }

    /** Hands the downloaded APK to Android's installer, which shows its prompt. */
    private fun install(context: Context, release: Release, apk: File) {
        installWhenReady = false
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val sessionId = installer.createSession(params)
        runCatching {
            installer.openSession(sessionId).use { session ->
                apk.inputStream().use { input ->
                    session.openWrite("Baton.apk", 0, apk.length()).use { output ->
                        input.copyTo(output)
                        session.fsync(output)
                    }
                }
                val done = PendingIntent.getBroadcast(context, sessionId, Intent(context, UpdateInstallReceiver::class.java),
                    PendingIntent.FLAG_MUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
                session.commit(done.intentSender)
            }
        }.onFailure {
            Log.w(TAG, "Update install failed", it)
            runCatching { installer.abandonSession(sessionId) }
            stateFlow.value = State.Failed(release, "Android didn't accept the update. Try again.")
        }
    }

    private fun latest(): Release? = runCatching {
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
    }.onFailure { Log.w(TAG, "Update check failed", it) }.getOrNull()

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
