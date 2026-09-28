package dev.baton.android.ui

import dev.baton.android.apps.Choices
import dev.baton.android.apps.AppCatalog
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
    val grants = rememberGrants()

    Surface(color = MaterialTheme.colorScheme.background, modifier = Modifier.fillMaxSize()) {
        when {
            !paired && !setupDone && !grants.essentialsDone -> SetupScreen(grants, onContinue = { setupDone = true })
            !paired -> PairScreen()
            showSettings -> SettingsScreen(trust, grants, onBack = { showSettings = false })
            else -> HomeScreen(grants, onSettings = { showSettings = true })
        }
        if (paired) ChoiceSheetHost()
    }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { ChoicePrompt.appVisible = true }
    LifecycleEventEffect(Lifecycle.Event.ON_PAUSE) { ChoicePrompt.appVisible = false }
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
private fun HomeScreen(grants: Grants, onSettings: () -> Unit) {
    val status by Link.status.collectAsState()
    val peers by Link.peers.collectAsState()
    val local by HandoffEngine.local.collectAsState()
    val pc = peers.firstOrNull { it.kind == "pc" }
    val otherPhones = peers.filter { it.kind != "pc" && it.online }
    val ready = status.phase == LinkPhase.Ready
    var notice by remember { mutableStateOf<HandoffNotice?>(null) }
    var pickApp by remember { mutableStateOf(false) }
    val preferences by Choices.preferences.collectAsState()
    val context = LocalContext.current
    if (pickApp) {
        AppPickerSheet(onDismiss = { pickApp = false }, onPick = { app ->
            pickApp = false
            HandoffEngine.continueWith(AppCatalog.activity(context, app), Link.deviceId, Link.hostId)
        })
    }

    LaunchedEffect(Unit) {
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
                modifier = Modifier.fillMaxSize()
            ) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("Baton", style = MaterialTheme.typography.headlineMedium, modifier = Modifier.weight(1f))
                        IconButton(onClick = onSettings) { Icon(Icons.Rounded.Settings, contentDescription = "Settings") }
                    }
                    Spacer(Modifier.height(Tokens.Space2))
                    StatusPill(status, pc?.name)
                }

                if (!grants.media || !grants.accessibility) {
                    item {
                        SectionTitle("Finish setting up")
                        Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space3)) { PermissionSteps(grants, onlyMissing = true) }
                    }
                }

                item { SectionTitle(if (pc != null) "On ${pc.name}" else "On your PC") }
                val pcActivities = pc?.activities.orEmpty()
                if (pcActivities.isEmpty()) {
                    item {
                        EmptyCard(if (ready) "Nothing playing or open on your PC right now." else "Connect to your PC to see what's on it.")
                    }
                }
                pcActivities.forEachIndexed { index, activity ->
                    item(key = "pc-${activity.id}") {
                        Box(Modifier.padding(bottom = Tokens.Space3)) {
                            val streams = activity.kind == ActivityKind.WindowStream ||
                                (activity.content?.provider == "unknown" && activity.window != null)
                            ActivityCard(activity, if (streams) "Watch here" else "Continue here",
                                if (streams) Icons.Rounded.Cast else Icons.Rounded.Smartphone,
                                onAction = { HandoffEngine.continueWith(activity, pc!!.deviceId, Link.deviceId) },
                                hero = index == 0, enabled = ready,
                                choiceLabel = preferences.let { rememberedLabel(activity, pc!!.deviceId, Link.deviceId) },
                                onChangeChoice = { HandoffEngine.continueWith(activity, pc!!.deviceId, Link.deviceId, ask = true) },
                                remote = RemoteControl { action, position, volume -> HandoffEngine.command(pc!!.deviceId, activity.id, action, position, volume) },
                                secondaryAction = if (!streams && activity.window != null) {
                                    CardAction("Stream", Icons.Rounded.Cast) { HandoffEngine.pull(pc!!.deviceId, activity.id, activity.title, HandoffModes.STREAM) }
                                } else null)
                        }
                    }
                }

                otherPhones.forEach { phone ->
                    item { SectionTitle("On ${phone.name}") }
                    phone.activities.take(2).forEach { activity ->
                        item(key = "${phone.deviceId}-${activity.id}") {
                            Box(Modifier.padding(bottom = Tokens.Space3)) {
                                ActivityCard(activity, "Continue here", Icons.Rounded.Smartphone,
                                    onAction = { HandoffEngine.pull(phone.deviceId, activity.id, activity.title) }, enabled = ready,
                                    remote = RemoteControl { action, position, volume -> HandoffEngine.command(phone.deviceId, activity.id, action, position, volume) })
                            }
                        }
                    }
                }

                item { SectionTitle("On this phone") }
                if (local.isEmpty()) {
                    item { EmptyCard("Play something or open a page, and it'll show up here, ready to send.") }
                }
                local.forEach { activity ->
                    item(key = "local-${activity.id}") {
                        Box(Modifier.padding(bottom = Tokens.Space3)) {
                            ActivityCard(activity, "Continue on ${pc?.name ?: "PC"}",
                                Icons.Rounded.Computer,
                                onAction = { HandoffEngine.continueWith(activity, Link.deviceId, Link.hostId) }, enabled = ready,
                                secondaryAction = if (activity.kind != ActivityKind.WindowStream) {
                                    CardAction("Mirror", Icons.Rounded.Cast) { HandoffEngine.sendTo(activityId = activity.id, mode = HandoffModes.STREAM) }
                                } else null,
                                choiceLabel = preferences.let { rememberedLabel(activity, Link.deviceId, Link.hostId) },
                                onChangeChoice = { HandoffEngine.continueWith(activity, Link.deviceId, Link.hostId, ask = true) })
                        }
                    }
                }
                item {
                    OutlinedButton(onClick = { pickApp = true }, enabled = ready, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Rounded.Apps, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(Tokens.Space2))
                        Text("Continue another app on ${pc?.name ?: "PC"}…")
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

// ---- Settings ----

@Composable
private fun SettingsScreen(trust: TrustStore, grants: Grants, onBack: () -> Unit) {
    val context = LocalContext.current
    var confirmForget by remember { mutableStateOf(false) }
    val host = remember { trust.host }
    androidx.activity.compose.BackHandler(onBack = onBack)

    LazyColumn(
        contentPadding = PaddingValues(Tokens.Space5),
        verticalArrangement = Arrangement.spacedBy(Tokens.Space3),
        modifier = Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()
    ) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back") }
                Text("Settings", style = MaterialTheme.typography.headlineMedium)
            }
        }
        item { SectionTitle("Paired PC") }
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
        item { SectionTitle("Handoff") }
        item { SuggestionSetting() }
        item { ScreenSharingSetting() }
        item { SectionTitle("App choices") }
        item { AppChoicesList() }
        item { SectionTitle("Permissions") }
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

    if (confirmForget) {
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text("Forget ${host?.pcName ?: "this PC"}?") },
            text = { Text("You'll need to pair again to continue things between this phone and the PC.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmForget = false
                    LinkService.forget(context)
                    onBack()
                }) { Text("Forget", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmForget = false }) { Text("Cancel") } }
        )
    }
}
