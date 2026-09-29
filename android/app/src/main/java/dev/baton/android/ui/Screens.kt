package dev.baton.android.ui

import dev.baton.android.apps.Choices
import dev.baton.android.apps.AppCatalog
import dev.baton.android.apps.AppUpdates
import androidx.compose.material.icons.rounded.Apps
import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Accessibility
import androidx.compose.material.icons.rounded.BatteryChargingFull
import androidx.compose.material.icons.rounded.Cast
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.PhoneAndroid
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.QueueMusic
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Smartphone
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import dev.baton.android.R
import dev.baton.android.handoff.BatonAccessibilityService
import dev.baton.android.handoff.BatonNotificationListener
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.link.BatteryOptimizationPrompt
import dev.baton.android.link.DiscoveredPc
import dev.baton.android.link.HandoffNotice
import dev.baton.android.link.LanDiscovery
import dev.baton.android.link.Link
import dev.baton.android.link.LinkPhase
import dev.baton.android.link.LinkService
import dev.baton.android.link.TrustStore
import dev.baton.android.protocol.ActivityKind
import dev.baton.android.protocol.HandoffModes
import dev.baton.android.protocol.HostEndpoint
import dev.baton.android.protocol.PairingLink
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Which setup steps are done; re-read every time the app comes back to the foreground. */
data class Grants(
    val notifications: Boolean,
    val media: Boolean,
    val accessibility: Boolean,
    val battery: Boolean
) {
    val essentialsDone: Boolean get() = notifications && media

    companion object {
        fun read(context: Context) = Grants(
            notifications = ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED,
            media = BatonNotificationListener.isGranted(context),
            accessibility = BatonAccessibilityService.isGranted(context),
            battery = BatteryOptimizationPrompt.isExempt(context)
        )
    }
}

@Composable
private fun rememberGrants(): Grants {
    val context = LocalContext.current
    var grants by remember { mutableStateOf(Grants.read(context)) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { grants = Grants.read(context) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(1_500)
            grants = Grants.read(context)
        }
    }
    return grants
}

@Composable
fun BatonApp(trust: TrustStore) {
    val status by Link.status.collectAsState()
    val paired = remember(status) { trust.isPaired }
    var showSettings by remember { mutableStateOf(false) }
    var setupDone by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val onboarding = remember { context.getSharedPreferences("onboarding", Context.MODE_PRIVATE) }
    var toured by remember { mutableStateOf(onboarding.getBoolean("toured", false)) }
    val grants = rememberGrants()

    Surface(color = MaterialTheme.colorScheme.background, modifier = Modifier.fillMaxSize()) {
        when {
            !paired && !setupDone && !grants.essentialsDone -> SetupScreen(grants, onContinue = { setupDone = true })
            !paired -> PairScreen()
            !toured -> TourScreen(remember(status) { trust.host?.pcName }) {
                onboarding.edit().putBoolean("toured", true).apply()
                toured = true
            }
            // Tablets and unfolded phones: home and settings side by side.
            LocalConfiguration.current.screenWidthDp >= 840 -> Row(Modifier.fillMaxSize()) {
                Box(Modifier.weight(1.2f)) { HomeScreen(grants, onSettings = null) }
                androidx.compose.material3.VerticalDivider()
                Box(Modifier.weight(1f)) { SettingsScreen(trust, grants, onBack = null) }
            }
            showSettings -> SettingsScreen(trust, grants, onBack = { showSettings = false })
            else -> HomeScreen(grants, onSettings = { showSettings = true })
        }
        if (paired) ChoiceSheetHost()
    }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { ChoicePrompt.appVisible = true }
    LifecycleEventEffect(Lifecycle.Event.ON_PAUSE) { ChoicePrompt.appVisible = false }
}

// ---- Updates ----

/** What the update button says for each stage, and the line under the version. */
private fun updateTexts(state: AppUpdates.State): Pair<String, String> = when (state) {
    AppUpdates.State.Idle -> "Check for updates" to
        if (dev.baton.android.BuildConfig.DEBUG) "Development build: it doesn't update from releases." else "Updates come from Baton's GitHub releases."
    AppUpdates.State.Checking -> "Checking…" to "Looking for a newer version…"
    AppUpdates.State.UpToDate -> "Check for updates" to "Baton is up to date."
    is AppUpdates.State.Available -> "Update to ${state.release.version}" to "Version ${state.release.version} is available."
    is AppUpdates.State.Downloading -> "Downloading ${state.percent}%" to "Downloading version ${state.release.version}…"
    is AppUpdates.State.Ready -> "Install ${state.release.version}" to "Version ${state.release.version} is ready to install."
    is AppUpdates.State.Failed -> "Try again" to state.message
}

/** The installed version, and a newer release to install when there is one. */
@Composable
private fun UpdateCard() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val state by AppUpdates.state.collectAsState()
    val (label, line) = updateTexts(state)
    Card(shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Tokens.Space4)) {
            Text("Baton ${AppUpdates.currentVersion}", style = MaterialTheme.typography.titleMedium)
            Text(line, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            (state as? AppUpdates.State.Downloading)?.let { downloading ->
                Spacer(Modifier.height(Tokens.Space2))
                androidx.compose.material3.LinearProgressIndicator(progress = { downloading.percent / 100f }, modifier = Modifier.fillMaxWidth())
            }
            Spacer(Modifier.height(Tokens.Space3))
            OutlinedButton(modifier = Modifier.fillMaxWidth(), enabled = state != AppUpdates.State.Checking, onClick = {
                if (AppUpdates.release != null) AppUpdates.update(context) else scope.launch { AppUpdates.check(context) }
            }) { Text(label) }
        }
    }
}

// ---- Setup ----

@Composable
private fun SetupScreen(grants: Grants, onContinue: () -> Unit) {
    LazyColumn(
        contentPadding = PaddingValues(Tokens.Space6),
        verticalArrangement = Arrangement.spacedBy(Tokens.Space3),
        modifier = Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()
    ) {
        item {
            Spacer(Modifier.height(Tokens.Space8))
            Image(painterResource(R.drawable.ic_launcher_foreground_art), contentDescription = null, modifier = Modifier.size(72.dp).clip(RoundedCornerShape(20.dp)))
            Spacer(Modifier.height(Tokens.Space5))
            StepHeader(1, "Permissions")
            Text("Pick up where you left off", style = MaterialTheme.typography.displaySmall)
            Spacer(Modifier.height(Tokens.Space2))
            Text(
                "Baton moves what you're watching, listening to or reading between this phone and your PC, right to the second.",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(Tokens.Space5))
        }
        item { PermissionSteps(grants) }
        item {
            Spacer(Modifier.height(Tokens.Space4))
            Button(onClick = onContinue, modifier = Modifier.fillMaxWidth().height(52.dp)) {
                Text(if (grants.essentialsDone) "Continue" else "Skip for now", style = MaterialTheme.typography.titleMedium)
            }
        }
    }
}

@Composable
private fun PermissionSteps(grants: Grants, onlyMissing: Boolean = false) {
    val context = LocalContext.current
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space3)) {
        if (!onlyMissing || !grants.notifications) PermissionCard(Icons.Rounded.Notifications, "Notifications",
            "Shows what you can continue, one tap away.", grants.notifications, "Allow") {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        if (!onlyMissing || !grants.media) PermissionCard(Icons.Rounded.QueueMusic, "Media access",
            "Lets Baton see what's playing and resume it at the same second.", grants.media, "Open") {
            context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        if (!onlyMissing || !grants.accessibility) PermissionCard(Icons.Rounded.Accessibility, "Instant open",
            "Opens things sent from your PC right away, and reads the page your browser shows.", grants.accessibility, "Open") {
            context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        if (!onlyMissing || !grants.battery) PermissionCard(Icons.Rounded.BatteryChargingFull, "Stay connected",
            "Keeps the link to your PC alive in the background.", grants.battery, "Allow") {
            BatteryOptimizationPrompt.requestExemption(context)
        }
    }
}

/** "Step 2 of 3 · Pair", above each onboarding screen's title. */
@Composable
private fun StepHeader(number: Int, name: String) {
    Text("Step $number of 3 · $name", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
    Spacer(Modifier.height(Tokens.Space2))
}

// ---- Tour ----

/** The last onboarding step: the three ways to move something, shown once after pairing. */
@Composable
private fun TourScreen(pcName: String?, onDone: () -> Unit) {
    val pc = pcName ?: "your PC"
    LazyColumn(
        contentPadding = PaddingValues(Tokens.Space6),
        verticalArrangement = Arrangement.spacedBy(Tokens.Space3),
        modifier = Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()
    ) {
        item {
            Spacer(Modifier.height(Tokens.Space6))
            StepHeader(3, "How it works")
            Text("You're connected to $pc", style = MaterialTheme.typography.headlineMedium)
            Spacer(Modifier.height(Tokens.Space5))
        }
        item { TourCard(Icons.Rounded.Notifications, "From the notification",
            "Baton's notification shows what you can move right now. Tap it: what plays here goes to $pc, or what $pc shows comes here.") }
        item { TourCard(Icons.Rounded.Computer, "From your PC",
            "Press Ctrl+Alt+→ on $pc to send what you're doing to this phone, and Ctrl+Alt+← to bring this phone's back.") }
        item { TourCard(Icons.Rounded.Smartphone, "From any app",
            "Share a video, song or page to Baton to open it on $pc.") }
        item {
            Spacer(Modifier.height(Tokens.Space4))
            Button(onClick = onDone, modifier = Modifier.fillMaxWidth().height(52.dp)) {
                Text("Start using Baton", style = MaterialTheme.typography.titleMedium)
            }
        }
    }
}

@Composable
private fun TourCard(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, text: String) {
    Card(shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(Tokens.Space4)) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
            Spacer(Modifier.width(Tokens.Space4))
            Column {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

// ---- Pairing ----

@Composable
private fun PairScreen() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val status by Link.status.collectAsState()
    var pcs by remember { mutableStateOf<List<DiscoveredPc>>(emptyList()) }
    var searching by remember { mutableStateOf(true) }
    var chosen by remember { mutableStateOf<DiscoveredPc?>(null) }
    var round by remember { mutableIntStateOf(0) }

    LaunchedEffect(round) {
        searching = true
        pcs = LanDiscovery.discover(2_000)
        searching = false
    }

    fun scan() {
        GmsBarcodeScanning.getClient(context).startScan()
            .addOnSuccessListener { barcode ->
                val raw = barcode.rawValue.orEmpty()
                PairingLink.parse(raw)?.let { LinkService.pair(context, it, raw) }
            }
    }

    LazyColumn(
        contentPadding = PaddingValues(Tokens.Space6),
        verticalArrangement = Arrangement.spacedBy(Tokens.Space3),
        modifier = Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()
    ) {
        item {
            Spacer(Modifier.height(Tokens.Space6))
            StepHeader(2, "Pair")
            Text("Pair with your PC", style = MaterialTheme.typography.headlineMedium)
            Spacer(Modifier.height(Tokens.Space2))
            Text(
                "On your PC, open Baton and choose Pair a phone. Then scan the code it shows.",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(Tokens.Space5))
            Button(onClick = ::scan, modifier = Modifier.fillMaxWidth().height(56.dp), enabled = status.phase != LinkPhase.Pairing) {
                Icon(Icons.Rounded.QrCodeScanner, contentDescription = null)
                Spacer(Modifier.width(Tokens.Space2))
                Text("Scan pairing code", style = MaterialTheme.typography.titleMedium)
            }
        }

        if (status.phase == LinkPhase.Pairing || status.detail.isNotBlank()) {
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
                    shape = RoundedCornerShape(Tokens.CardRadius), modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(Tokens.Space4), verticalAlignment = Alignment.CenterVertically) {
                        if (status.phase == LinkPhase.Pairing) {
                            CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                            Spacer(Modifier.width(Tokens.Space3))
                        }
                        Text(status.detail.ifBlank { "Pairing…" }, style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
        }

        item {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = Tokens.Space5)) {
                Text("Or pick your PC", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f))
                if (searching) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                } else {
                    IconButton(onClick = { round++ }) { Icon(Icons.Rounded.Refresh, contentDescription = "Search again") }
                }
            }
        }

        if (!searching && pcs.isEmpty()) {
            item { EmptyCard("No PC found on this Wi-Fi. Make sure Baton is running on your PC and both are on the same network.") }
        }

        pcs.forEach { pc ->
            item(key = pc.hostId) {
                Card(
                    shape = RoundedCornerShape(Tokens.CardRadius),
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
                    modifier = Modifier.fillMaxWidth().clickable { chosen = pc }
                ) {
                    Row(Modifier.padding(Tokens.Space4), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Rounded.Computer, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.width(Tokens.Space4))
                        Column(Modifier.weight(1f)) {
                            Text(pc.name, style = MaterialTheme.typography.titleMedium)
                            Text(pc.address, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }

    chosen?.let { pc ->
        CodeDialog(pc, onDismiss = { chosen = null }) { code ->
            chosen = null
            val link = PairingLink(pc.hostId, pc.name, pc.fingerprint, code, listOf(HostEndpoint(pc.address, pc.port)))
            scope.launch { LinkService.pair(context, link, link.toUri()) }
        }
    }
}

@Composable
private fun CodeDialog(pc: DiscoveredPc, onDismiss: () -> Unit, onPair: (String) -> Unit) {
    var code by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pair with ${pc.name}") },
        text = {
            Column {
                Text("Enter the 6-digit code Baton shows on your PC.", style = MaterialTheme.typography.bodyMedium)
                Spacer(Modifier.height(Tokens.Space4))
                OutlinedTextField(
                    value = code,
                    onValueChange = { value -> code = value.filter { it.isDigit() }.take(6) },
                    singleLine = true,
                    textStyle = MaterialTheme.typography.headlineMedium.copy(textAlign = TextAlign.Center, letterSpacing = 6.sp),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = { Button(onClick = { onPair(code) }, enabled = code.length == 6) { Text("Pair") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } }
    )
}

// ---- Home ----

@Composable
private fun HomeScreen(grants: Grants, onSettings: (() -> Unit)?) {
    val status by Link.status.collectAsState()
    val peers by Link.peers.collectAsState()
    val local by HandoffEngine.local.collectAsState()
    val pc = peers.firstOrNull { it.kind == "pc" }
    val otherPhones = peers.filter { it.kind != "pc" && it.online }
    val ready = status.phase == LinkPhase.Ready
    var notice by remember { mutableStateOf<HandoffNotice?>(null) }
    var pickApp by remember { mutableStateOf(false) }
    var showMore by remember { mutableStateOf(false) }
    val updateState by AppUpdates.state.collectAsState()
    val scope = rememberCoroutineScope()
    val preferences by Choices.preferences.collectAsState()
    val context = LocalContext.current
    if (pickApp) {
        AppPickerSheet(onDismiss = { pickApp = false }, onPick = { app ->
            pickApp = false
            HandoffEngine.continueWith(AppCatalog.activity(context, app), Link.deviceId, Link.hostId)
        })
    }

    LaunchedEffect(Unit) {
        launch { AppUpdates.check(context) }
        HandoffEngine.refresh()
        Link.notices.collect { incoming ->
            notice = incoming.copy(title = incoming.title.ifBlank { notice?.title.orEmpty() })
        }
    }
    LaunchedEffect(notice) {
        if (notice?.done == true) {
            delay(4_000)
            notice = null
        }
    }

    Scaffold(containerColor = MaterialTheme.colorScheme.background) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            LazyColumn(
                contentPadding = PaddingValues(start = Tokens.Space5, end = Tokens.Space5, top = Tokens.Space4, bottom = 120.dp),
                modifier = Modifier.fillMaxSize().widthIn(max = 720.dp).align(Alignment.TopCenter)
            ) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("Baton", style = MaterialTheme.typography.headlineMedium, modifier = Modifier.weight(1f))
                        if (onSettings != null) IconButton(onClick = onSettings) { Icon(Icons.Rounded.Settings, contentDescription = "Settings") }
                    }
                    Spacer(Modifier.height(Tokens.Space2))
                    StatusPill(status, pc?.name)
                }

                if (AppUpdates.release != null) {
                    item {
                        val (label, line) = updateTexts(updateState)
                        Card(shape = RoundedCornerShape(Tokens.CardRadius),
                            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer),
                            modifier = Modifier.fillMaxWidth().padding(top = Tokens.Space3)) {
                            Row(Modifier.padding(start = Tokens.Space4, end = Tokens.Space2), verticalAlignment = Alignment.CenterVertically) {
                                Text(line, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                                TextButton(onClick = { AppUpdates.update(context) }) { Text(label) }
                            }
                        }
                    }
                }

                if (!grants.media || !grants.accessibility) {
                    item {
                        SectionTitle("Finish setting up")
                        Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space3)) { PermissionSteps(grants, onlyMissing = true) }
                    }
                }

                // One "now": what the user most likely moves next. What this phone plays goes to the PC;
                // otherwise what the PC has comes here. Everything else waits under More.
                val pcActivities = pc?.activities.orEmpty()
                val localFirst = local.firstOrNull()?.playback?.playing == true || pcActivities.isEmpty()
                val pcNow = pcActivities.firstOrNull()
                val localNow = local.firstOrNull()

                @Composable
                fun PcCard(activity: dev.baton.android.protocol.Activity, hero: Boolean) {
                    val streams = activity.kind == ActivityKind.WindowStream ||
                        (activity.content?.provider == "unknown" && activity.window != null)
                    ActivityCard(activity, if (streams) "Watch here" else "Continue here",
                        if (streams) Icons.Rounded.Cast else Icons.Rounded.Smartphone,
                        onAction = { HandoffEngine.continueWith(activity, pc!!.deviceId, Link.deviceId) },
                        hero = hero, enabled = ready,
                        choiceLabel = preferences.let { rememberedLabel(activity, pc!!.deviceId, Link.deviceId) },
                        onChangeChoice = { HandoffEngine.continueWith(activity, pc!!.deviceId, Link.deviceId, ask = true) },
                        // Only the "now" card carries remote controls; the rest stay one line of buttons.
                        remote = if (hero) RemoteControl { action, position, volume -> HandoffEngine.command(pc!!.deviceId, activity.id, action, position, volume) } else null,
                        secondaryAction = if (!streams && activity.window != null) {
                            CardAction("Stream", Icons.Rounded.Cast) { HandoffEngine.pull(pc!!.deviceId, activity.id, activity.title, HandoffModes.STREAM) }
                        } else null)
                }

                @Composable
                fun LocalCard(activity: dev.baton.android.protocol.Activity, hero: Boolean) {
                    ActivityCard(activity, "Continue on PC",
                        Icons.Rounded.Computer,
                        onAction = { HandoffEngine.continueWith(activity, Link.deviceId, Link.hostId) }, hero = hero, enabled = ready,
                        secondaryAction = if (activity.kind != ActivityKind.WindowStream) {
                            CardAction("Mirror", Icons.Rounded.Cast) { HandoffEngine.sendTo(activityId = activity.id, mode = HandoffModes.STREAM) }
                        } else null,
                        choiceLabel = preferences.let { rememberedLabel(activity, Link.deviceId, Link.hostId) },
                        onChangeChoice = { HandoffEngine.continueWith(activity, Link.deviceId, Link.hostId, ask = true) })
                }

                val nowCards = buildList<Pair<String, @Composable (Boolean) -> Unit>> {
                    val pcEntry = pcNow?.let { activity -> "On ${pc?.name ?: "your PC"}" to @Composable { hero: Boolean -> PcCard(activity, hero) } }
                    val localEntry = localNow?.let { activity -> "On this phone" to @Composable { hero: Boolean -> LocalCard(activity, hero) } }
                    if (localFirst) { localEntry?.let(::add); pcEntry?.let(::add) } else { pcEntry?.let(::add); localEntry?.let(::add) }
                }
                if (nowCards.isEmpty()) {
                    item {
                        SectionTitle("Now")
                        EmptyCard(if (ready) "Play something or open a page here or on your PC, and it'll show up here, ready to move."
                            else "Connect to your PC to see what's on it.")
                    }
                }
                nowCards.forEachIndexed { index, (title, card) ->
                    item(key = "now-$title") {
                        SectionTitle(title)
                        Box(Modifier.padding(bottom = Tokens.Space3)) { card(index == 0) }
                    }
                }

                val morePc = pcActivities.drop(1)
                val moreLocal = local.drop(1)
                val phoneItems = otherPhones.sumOf { it.activities.take(2).size }
                item {
                    TextButton(onClick = { showMore = !showMore }, modifier = Modifier.fillMaxWidth()) {
                        Text(if (showMore) "Less" else if (morePc.size + moreLocal.size + phoneItems > 0) "More (${morePc.size + moreLocal.size + phoneItems})" else "More")
                    }
                }
                item { RecentHandoffs() }
                if (showMore) {
                    if (morePc.isNotEmpty()) item { SectionTitle("Also on ${pc?.name ?: "your PC"}") }
                    morePc.forEach { activity ->
                        item(key = "pc-${activity.id}") { Box(Modifier.padding(bottom = Tokens.Space3)) { PcCard(activity, false) } }
                    }
                    otherPhones.forEach { phone ->
                        if (phone.activities.isNotEmpty()) item { SectionTitle("On ${phone.name}") }
                        phone.activities.take(2).forEach { activity ->
                            item(key = "${phone.deviceId}-${activity.id}") {
                                Box(Modifier.padding(bottom = Tokens.Space3)) {
                                    ActivityCard(activity, "Continue here", Icons.Rounded.Smartphone,
                                        onAction = { HandoffEngine.pull(phone.deviceId, activity.id, activity.title) }, enabled = ready)
                                }
                            }
                        }
                    }
                    if (moreLocal.isNotEmpty()) item { SectionTitle("Also on this phone") }
                    moreLocal.forEach { activity ->
                        item(key = "local-${activity.id}") { Box(Modifier.padding(bottom = Tokens.Space3)) { LocalCard(activity, false) } }
                    }
                    item {
                        OutlinedButton(onClick = { pickApp = true }, enabled = ready, modifier = Modifier.fillMaxWidth()) {
                            Icon(Icons.Rounded.Apps, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(Tokens.Space2))
                            Text("Continue another app on ${pc?.name ?: "PC"}…")
                        }
                    }
                }
            }

            AnimatedVisibility(
                visible = notice != null,
                enter = slideInVertically { it } + fadeIn(),
                exit = slideOutVertically { it } + fadeOut(),
                modifier = Modifier.align(Alignment.BottomCenter).padding(Tokens.Space4)
            ) {
                notice?.let { NoticeBanner(it) }
            }
        }
    }
}

@Composable
private fun NoticeBanner(notice: HandoffNotice) {
    Surface(
        shape = RoundedCornerShape(Tokens.CardRadius),
        color = if (notice.failed) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.inverseSurface,
        shadowElevation = 6.dp,
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(Modifier.padding(Tokens.Space4), verticalAlignment = Alignment.CenterVertically) {
            if (!notice.done) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.inverseOnSurface)
                Spacer(Modifier.width(Tokens.Space3))
            }
            Column {
                if (notice.title.isNotBlank()) {
                    Text(notice.title, fontWeight = FontWeight.SemiBold, maxLines = 1,
                        color = if (notice.failed) MaterialTheme.colorScheme.onErrorContainer else MaterialTheme.colorScheme.inverseOnSurface)
                }
                Text(notice.text, style = MaterialTheme.typography.bodyMedium,
                    color = if (notice.failed) MaterialTheme.colorScheme.onErrorContainer else MaterialTheme.colorScheme.inverseOnSurface)
            }
        }
    }
}

@Composable
private fun RecentHandoffs() {
    val records by HandoffEngine.history.recent.collectAsState()
    val peers by Link.peers.collectAsState()
    val local by HandoffEngine.local.collectAsState()
    val status by Link.status.collectAsState()
    var expanded by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf<Pair<String, Boolean>?>(null) }
    fun name(id: String) = if (id == Link.deviceId) "This phone" else peers.firstOrNull { it.deviceId == id }?.name ?: "Unavailable device"
    Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space2)) {
        TextButton(onClick = { expanded = !expanded }, modifier = Modifier.fillMaxWidth()) {
            Text("Recent handoffs (${records.size}) · ${if (expanded) "Hide" else "Show"}")
        }
        if (expanded) {
            TextButton(onClick = { HandoffEngine.history.clear() }, modifier = Modifier.align(Alignment.End)) { Text("Clear history") }
            if (records.isEmpty()) Text("No handoffs in this session.", color = MaterialTheme.colorScheme.onSurfaceVariant)
            records.forEach { record ->
                val availability = remember(record, peers, local, status) { HandoffEngine.recovery(record) }
                val couldStream = record.intent.source == Link.deviceId || record.intent.activity?.window != null ||
                    peers.any { it.deviceId == record.intent.source && it.kind == "phone" }
                Card(shape = RoundedCornerShape(Tokens.CardRadius), modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(Tokens.Space4), verticalArrangement = Arrangement.spacedBy(Tokens.Space2)) {
                        Text(record.title, style = MaterialTheme.typography.titleMedium)
                        Text("${java.text.DateFormat.getTimeInstance(java.text.DateFormat.SHORT).format(java.util.Date(record.startedAt))} · ${name(record.intent.source)} → ${name(record.intent.target)}",
                            style = MaterialTheme.typography.bodySmall)
                        Text(record.outcome)
                        Text(record.detail, style = MaterialTheme.typography.bodySmall)
                        if (record.recoverable || record.status == dev.baton.android.protocol.HandoffStatus.Fallback) {
                            availability.third?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                            Row(horizontalArrangement = Arrangement.spacedBy(Tokens.Space2)) {
                                if (record.recoverable) OutlinedButton(enabled = availability.first, onClick = {
                                    if (record.unconfirmed) confirm = record.requestId to false else HandoffEngine.recover(record.requestId)
                                }) { Text("Retry") }
                                if (couldStream) OutlinedButton(enabled = availability.second, onClick = {
                                    if (record.unconfirmed) confirm = record.requestId to true else HandoffEngine.recover(record.requestId, true)
                                }) { Text("Stream instead") }
                            }
                        }
                    }
                }
            }
        }
    }
    confirm?.let { request ->
        AlertDialog(onDismissRequest = { confirm = null }, title = { Text("Retry handoff?") },
            text = { Text("The destination hasn't confirmed this transfer. The activity may already have opened. Try again?") },
            confirmButton = { TextButton(onClick = { confirm = null; HandoffEngine.recover(request.first, request.second) }) { Text("Try again") } },
            dismissButton = { TextButton(onClick = { confirm = null }) { Text("Cancel") } })
    }
}

// ---- Settings ----

/** The settings screens, reached from one list, as Android's own settings are. */
private enum class SettingsPage(val title: String, val summary: String) {
    Handoff("Handoff", "Suggestions when you pick up this phone"),
    Sharing("Showing this phone", "How your PC shows this phone's screen"),
    Choices("App choices", "Which app opens what you continue"),
    Permissions("Permissions", "What Baton is allowed to do"),
    About("About", "Your PC, version and updates")
}

@Composable
fun SettingsScreen(trust: TrustStore, grants: Grants, onBack: (() -> Unit)?) {
    val context = LocalContext.current
    var confirmForget by remember { mutableStateOf(false) }
    var page by remember { mutableStateOf<SettingsPage?>(null) }
    val host = remember { trust.host }
    androidx.activity.compose.BackHandler(enabled = page != null || onBack != null) {
        if (page != null) page = null else onBack?.invoke()
    }

    LazyColumn(
        contentPadding = PaddingValues(Tokens.Space5),
        verticalArrangement = Arrangement.spacedBy(Tokens.Space3),
        modifier = Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()
    ) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (page != null || onBack != null) {
                    IconButton(onClick = { if (page != null) page = null else onBack?.invoke() }) {
                        Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back")
                    }
                }
                Text(page?.title ?: "Settings", style = MaterialTheme.typography.headlineMedium)
            }
        }
        when (page) {
            null -> SettingsPage.entries.forEach { entry ->
                item(key = entry.name) {
                    Card(shape = RoundedCornerShape(Tokens.CardRadius),
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
                        modifier = Modifier.fillMaxWidth().clickable { page = entry }) {
                        Column(Modifier.padding(Tokens.Space4)) {
                            Text(entry.title, style = MaterialTheme.typography.titleMedium)
                            Text(entry.summary,
                                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
            SettingsPage.Handoff -> item { SuggestionSetting() }
            SettingsPage.Sharing -> item { ScreenSharingSetting() }
            SettingsPage.Choices -> item { AppChoicesList() }
            SettingsPage.Permissions -> {
                item { PermissionSteps(grants) }
                item {
                    if (BatteryOptimizationPrompt.isOneUi()) {
                        TextButton(onClick = { BatteryOptimizationPrompt.openOneUiSleepingApps(context) }) {
                            Text("Samsung: keep Baton out of sleeping apps")
                        }
                    }
                    TextButton(onClick = {
                        context.startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}")))
                    }) { Text("Allow display over other apps (instant open without accessibility)") }
                }
            }
            SettingsPage.About -> {
              item {
                Card(shape = RoundedCornerShape(Tokens.CardRadius),
                    colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh)) {
                    Column(Modifier.padding(Tokens.Space4)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Rounded.Computer, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                            Spacer(Modifier.width(Tokens.Space4))
                            Column(Modifier.weight(1f)) {
                                Text(host?.pcName ?: "PC", style = MaterialTheme.typography.titleMedium)
                                Text("This phone: ${trust.displayName}", style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                        Spacer(Modifier.height(Tokens.Space3))
                        HorizontalDivider()
                        Spacer(Modifier.height(Tokens.Space2))
                        OutlinedButton(onClick = { confirmForget = true }, modifier = Modifier.fillMaxWidth()) { Text("Forget this PC") }
                    }
                }
            }
              item { UpdateCard() }
            }
        }
    }

    if (confirmForget) {
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text("Forget ${host?.pcName ?: "this PC"}?") },
            text = { Text("You'll need to pair again to continue things between this phone and the PC.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmForget = false
                    LinkService.forget(context)
                    page = null
                    onBack?.invoke()
                }) { Text("Forget", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmForget = false }) { Text("Cancel") } }
        )
    }
}
