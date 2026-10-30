package dev.baton.android.ui

import androidx.lifecycle.compose.collectAsStateWithLifecycle
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import dev.baton.android.apps.AppCatalog
import dev.baton.android.BatonSettings
import dev.baton.android.BatonSettings.MirrorMode
import dev.baton.android.handoff.BatonAccessibilityService
import dev.baton.android.link.LinkDiagnostics
import dev.baton.android.link.LinkEvent
import dev.baton.android.mirror.PhoneMirror
import dev.baton.android.mirror.ShizukuAccess

@Composable
private fun SettingsCardColumn(content: @Composable () -> Unit) {
    Card(shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh)) {
        Column(Modifier.padding(Tokens.Space4)) { content() }
    }
}

/** "Offer to continue from my PC when I unlock". */
@Composable
fun SuggestionSetting() {
    val context = LocalContext.current
    var enabled by remember { mutableStateOf(BatonSettings.suggestFromPc(context)) }
    SettingsCardColumn {
        Row(verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().clickable {
                enabled = !enabled
                BatonSettings.setSuggestFromPc(context, enabled)
            }) {
            Column(Modifier.weight(1f)) {
                Text("Offer to continue from my PC", style = MaterialTheme.typography.titleMedium)
                Text("When you unlock this phone after leaving the PC, suggest what it was playing or showing.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Spacer(Modifier.width(Tokens.Space3))
            Switch(checked = enabled, onCheckedChange = {
                enabled = it
                BatonSettings.setSuggestFromPc(context, it)
            })
        }
    }
}

/** Three-finger swipes: up sends to the PC, down continues the PC's activity here; off per app. */
@Composable
fun SwipeSetting() {
    val context = LocalContext.current
    var enabled by remember { mutableStateOf(BatonSettings.swipes(context)) }
    var offApps by remember { mutableStateOf(BatonSettings.swipesOffApps(context)) }
    val shizuku by ShizukuAccess.state.collectAsStateWithLifecycle()
    var setupOpen by remember { mutableStateOf(false) }
    var picking by remember { mutableStateOf(false) }
    val names = remember { AppCatalog.apps(context).associate { it.id to it.name } }

    fun setOffApps(next: Set<String>) {
        offApps = next
        BatonSettings.setSwipesOffApps(context, next)
    }

    SettingsCardColumn {
        Row(verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().clickable {
                enabled = !enabled
                BatonSettings.setSwipes(context, enabled)
                ShizukuAccess.swipesChanged()
            }) {
            Column(Modifier.weight(1f)) {
                Text("Three-finger swipes", style = MaterialTheme.typography.titleMedium)
                Text("Swipe up with three fingers to send what's on this phone to your PC; swipe down to continue your PC's " +
                    "activity here. The app underneath also sees the swipe. Uses the free Shizuku app.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Spacer(Modifier.width(Tokens.Space3))
            Switch(checked = enabled, onCheckedChange = {
                enabled = it
                BatonSettings.setSwipes(context, it)
                ShizukuAccess.swipesChanged()
            })
        }
        if (enabled && shizuku != ShizukuAccess.State.Ready) {
            HorizontalDivider(Modifier.padding(vertical = Tokens.Space2))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(if (shizuku == ShizukuAccess.State.NotRunning) "Shizuku isn't running, so swipes do nothing for now."
                    else "Swipes need Shizuku.", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                Spacer(Modifier.width(Tokens.Space3))
                Button(onClick = { setupOpen = true }) { Text("Set up") }
            }
        }
        if (enabled) {
            HorizontalDivider(Modifier.padding(vertical = Tokens.Space2))
            Text("Off in these apps", style = MaterialTheme.typography.bodyLarge)
            offApps.sortedBy { (names[it] ?: it).lowercase() }.forEach { packageName ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(names[packageName] ?: packageName, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                    IconButton(onClick = { setOffApps(offApps - packageName) }) {
                        Icon(Icons.Rounded.Close, contentDescription = "Turn swipes back on in ${names[packageName] ?: packageName}")
                    }
                }
            }
            TextButton(onClick = { picking = true }, contentPadding = PaddingValues(0.dp)) { Text("Add app") }
        }
    }

    if (setupOpen) ShizukuSetupDialog(onClose = { setupOpen = false })
    if (picking) {
        AppPickerSheet(onDismiss = { picking = false }, title = "Turn swipes off in") { app ->
            picking = false
            setOffApps(offApps + app.id)
        }
    }
}

/** Whether this phone has Baton's Edge panel: One UI, and a build made with Samsung's Look SDK. */
fun hasEdgePanel(context: android.content.Context): Boolean =
    dev.baton.android.link.BatteryOptimizationPrompt.isOneUi() && context.resources.getBoolean(dev.baton.android.R.bool.edge_panel)

/** Where to find Baton's Edge panel (Samsung phones only). */
@Composable
fun EdgePanelHint() {
    SettingsCardColumn {
        Text("Edge panel", style = MaterialTheme.typography.titleMedium)
        Text("Add Baton's panel in Settings › Display › Edge panels › Panels. It has buttons to send to your PC and to " +
            "continue your PC's activity here.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** How showing this phone on the PC gets Android's screen-sharing consent. */
@Composable
fun ScreenSharingSetting() {
    val context = LocalContext.current
    var mode by remember { mutableStateOf(BatonSettings.mirrorMode(context)) }
    val shizuku by ShizukuAccess.state.collectAsStateWithLifecycle()
    val accessibilityOn by BatonAccessibilityService.connected.collectAsStateWithLifecycle()

    var setupOpen by remember { mutableStateOf(false) }

    fun choose(next: MirrorMode) {
        val previous = mode
        if (next == previous) return
        mode = next
        BatonSettings.setMirrorMode(context, next)
        if (previous == MirrorMode.KeepReady) PhoneMirror.release(context)
        ShizukuAccess.modeChanged(previous, next)
        if (next == MirrorMode.Shizuku && shizuku != ShizukuAccess.State.Ready) setupOpen = true
    }

    SettingsCardColumn {
        Text("Showing this phone on your PC", style = MaterialTheme.typography.titleMedium)
        ModeOption(mode == MirrorMode.AskEachTime, "Ask each time",
            "Android asks before every handoff.") { choose(MirrorMode.AskEachTime) }
        ModeOption(mode == MirrorMode.KeepReady, "Keep ready",
            "Android asks once. Screen sharing then stays ready between handoffs (the cast icon stays in the status bar) " +
                "until you stop it, restart the phone, or 4 hours pass unused. Nothing is recorded or sent in between.") { choose(MirrorMode.KeepReady) }
        ModeOption(mode == MirrorMode.Shizuku, "Shizuku",
            "Never asks, and your PC's input arrives as real touches. Uses the free Shizuku app; Baton walks you " +
                "through setting it up once. The cast icon still shows while the PC shows this phone.") { choose(MirrorMode.Shizuku) }

        if (mode == MirrorMode.Shizuku) {
            HorizontalDivider(Modifier.padding(vertical = Tokens.Space2))
            if (shizuku == ShizukuAccess.State.Ready) {
                ShizukuReady(accessibilityOn, onSetUpAgain = { setupOpen = true })
            } else {
                val (status, action) = when (shizuku) {
                    ShizukuAccess.State.NotInstalled -> "Shizuku isn't set up yet." to "Set up"
                    ShizukuAccess.State.NotAllowed -> "Baton still needs your OK in Shizuku." to "Continue"
                    else -> "Shizuku isn't running, so your PC's input uses the accessibility service for now." to "Start"
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(status, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                    Spacer(Modifier.width(Tokens.Space3))
                    Button(onClick = { setupOpen = true }) { Text(action) }
                }
            }
        }
    }

    if (setupOpen) ShizukuSetupDialog(onClose = { setupOpen = false })
}

@Composable
private fun ShizukuReady(accessibilityOn: Boolean, onSetUpAgain: () -> Unit) {
    val context = LocalContext.current
    val onBoot = remember { ShizukuAccess.startsOnBoot(context) }
    Text("Shizuku is ready.", style = MaterialTheme.typography.bodyMedium)
    if (onBoot) {
        Text("It starts by itself after a restart, on a Wi-Fi where Wireless debugging is always allowed.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if (!accessibilityOn) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = Tokens.Space3)) {
            Text("Turn on Baton's accessibility service for typing from your PC.",
                style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
            Spacer(Modifier.width(Tokens.Space3))
            OutlinedButton(onClick = { ShizukuAccess.enableAccessibility(context) }) { Text("Turn on") }
        }
    }
    var wirelessOff by remember { mutableStateOf(BatonSettings.wirelessDebuggingOff(context)) }
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = Tokens.Space3)) {
        Column(Modifier.weight(1f)) {
            Text("Turn off Wireless debugging", style = MaterialTheme.typography.bodyLarge)
            Text("Once Shizuku runs. It only needs Wireless debugging to start.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Spacer(Modifier.width(Tokens.Space3))
        Switch(checked = wirelessOff, onCheckedChange = {
            wirelessOff = it
            BatonSettings.setWirelessDebuggingOff(context, it)
            ShizukuAccess.wirelessDebuggingSettingChanged()
        })
    }
    TextButton(onClick = onSetUpAgain, contentPadding = PaddingValues(0.dp)) { Text("Setup steps") }
}

@Composable
private fun ModeOption(selected: Boolean, title: String, description: String, onClick: () -> Unit) {
    Row(verticalAlignment = Alignment.Top,
        modifier = Modifier.fillMaxWidth().selectable(selected = selected, onClick = onClick, role = Role.RadioButton)
            .padding(vertical = Tokens.Space2)) {
        RadioButton(selected = selected, onClick = null)
        Spacer(Modifier.width(Tokens.Space3))
        Column {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            Text(description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** The link log: why the connection dropped, with a report to copy or share. */
@Composable
fun LinkDiagnosticsSetting() {
    val context = LocalContext.current
    remember { LinkDiagnostics.init(context) }
    val events by LinkDiagnostics.events.collectAsStateWithLifecycle()
    var copied by remember { mutableStateOf(false) }
    val causes = remember(events) { LinkEvent.causes(events, System.currentTimeMillis() - 24L * 60L * 60L * 1000L) }

    SettingsCardColumn {
        Text("Connection log", style = MaterialTheme.typography.titleMedium)
        Text("Every connect, drop, network change and Doze change on this phone. It stays on this phone until you " +
            "copy or share the report.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(Tokens.Space3))
        Text(if (causes.isEmpty()) "No drops in the last 24 hours."
            else "Drops in the last 24 hours: " + causes.joinToString(", ") { "${it.first} ×${it.second}" },
            style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(Tokens.Space3))
        Row(horizontalArrangement = Arrangement.spacedBy(Tokens.Space2)) {
            Button(onClick = {
                context.getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newPlainText("Baton link report", LinkDiagnostics.report(context)))
                copied = true
            }) { Text(if (copied) "Copied" else "Copy report") }
            OutlinedButton(onClick = {
                val send = Intent(Intent.ACTION_SEND).setType("text/plain")
                    .putExtra(Intent.EXTRA_SUBJECT, "Baton link report")
                    .putExtra(Intent.EXTRA_TEXT, LinkDiagnostics.report(context))
                context.startActivity(Intent.createChooser(send, "Share link report"))
            }) { Text("Share") }
            TextButton(onClick = { LinkDiagnostics.clear() }) { Text("Clear") }
        }
        if (events.isNotEmpty()) {
            HorizontalDivider(Modifier.padding(vertical = Tokens.Space3))
            events.sortedByDescending { it.lastAt }.take(RECENT_EVENTS).forEach {
                Text(LinkDiagnostics.line(it), style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace,
                    color = if (it.event.startsWith("drop.") || LinkEvent.isUnexpectedExit(it)) MaterialTheme.colorScheme.error
                        else MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 2.dp))
            }
        }
    }
}

private const val RECENT_EVENTS = 40
