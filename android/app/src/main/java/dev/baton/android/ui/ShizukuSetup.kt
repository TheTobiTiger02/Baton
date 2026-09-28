package dev.baton.android.ui

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import dev.baton.android.BatonSettings
import dev.baton.android.handoff.BatonAccessibilityService
import dev.baton.android.mirror.ShizukuAccess

/**
 * Walks through getting Shizuku running, one step at a time, and moves on by itself as each step
 * is done: the phone is checked again whenever the user comes back from Shizuku or Settings.
 * Everything after the first start (starting after a restart, Wireless debugging off again) is
 * Baton's and Shizuku's job, not the user's.
 */
@Composable
fun ShizukuSetupDialog(onClose: () -> Unit) {
    val context = LocalContext.current
    val shizuku by ShizukuAccess.state.collectAsState()
    val accessibilityOn by BatonAccessibilityService.connected.collectAsState()
    var developerOptions by remember { mutableStateOf(developerOptionsOn(context)) }
    val setUpBefore = remember { ShizukuAccess.hasRunBefore(context) }
    var showPairing by remember { mutableStateOf(false) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) {
        developerOptions = developerOptionsOn(context)
        ShizukuAccess.refresh()
    }
    val step = currentStep(shizuku, developerOptions)
    // Shizuku's own question comes up by itself once it runs; the button is there if it was dismissed.
    LaunchedEffect(step) { if (step == Step.Allow) ShizukuAccess.requestPermission() }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.verticalScroll(rememberScrollState()).padding(Tokens.Space5)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Set up Shizuku", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.weight(1f))
                    IconButton(onClick = onClose) { Icon(Icons.Rounded.Close, contentDescription = "Close") }
                }
                Text(
                    "Shizuku lets Baton show this phone on your PC without asking every time, and turns your PC's " +
                        "mouse into real touches. You set it up once; after that it starts by itself.",
                    style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(Tokens.Space5))

                Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space3)) {
                    StepCard(1, "Install Shizuku", step, Step.Install) {
                        Instruction("Shizuku is a free app. It gives Baton what a computer connection would, without a computer.")
                        Action("Get Shizuku") { openStore(context) }
                    }
                    StepCard(2, "Turn on Developer options", step, Step.DeveloperOptions) {
                        Instruction(
                            if (Build.MANUFACTURER.equals("samsung", ignoreCase = true)) {
                                "Open About phone, tap Software information, then tap Build number 7 times until it " +
                                    "says Developer mode has been turned on. Then come back here."
                            } else {
                                "Open About phone and tap Build number 7 times until it says you are now a developer. " +
                                    "Then come back here."
                            }
                        )
                        Action("Open About phone") { open(context, Intent(Settings.ACTION_DEVICE_INFO_SETTINGS)) }
                    }
                    StepCard(3, if (setUpBefore) "Start Shizuku" else "Pair and start Shizuku", step, Step.PairAndStart) {
                        if (setUpBefore && !showPairing) {
                            Instruction("Shizuku is set up already. Open it and tap Start; Baton moves on by itself.")
                            Action("Open Shizuku") { ShizukuAccess.openShizuku(context) }
                            TextButton(onClick = { showPairing = true }) { Text("Start doesn't work? Pair again") }
                        } else {
                            Instruction("Once only, with Wi-Fi on:")
                            Numbered(1, "Turn on Wireless debugging. When Android asks, tick “Always allow on this network” " +
                                "and tap Allow, so Shizuku can start by itself on this Wi-Fi later.")
                            Action("Open Wireless debugging") { openWirelessDebugging(context) }
                            Numbered(2, "In Shizuku, tap Pairing. Then, in Wireless debugging, tap “Pair device with pairing " +
                                "code” and type the six digits into Shizuku's notification.")
                            Numbered(3, "In Shizuku, tap Start. Baton moves on by itself.")
                            Action("Open Shizuku") { ShizukuAccess.openShizuku(context) }
                        }
                    }
                    StepCard(4, "Let Baton use Shizuku", step, Step.Allow) {
                        Instruction("Shizuku asks whether Baton may use it. Tap “Allow all the time”.")
                        Action("Ask again") { ShizukuAccess.requestPermission() }
                    }
                }

                if (step == Step.Done) {
                    Spacer(Modifier.height(Tokens.Space5))
                    Card(shape = RoundedCornerShape(Tokens.CardRadius),
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer)) {
                        Column(Modifier.padding(Tokens.Space4)) {
                            Text("All set", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                            Text(
                                (if (BatonSettings.wirelessDebuggingOff(context)) "Baton turns Wireless debugging off again, and " else "") +
                                    "Shizuku starts by itself after a restart on this Wi-Fi. If it ever isn't running, " +
                                    "your PC still shows this phone without asking; only the real touches wait until " +
                                    "you tap Start in Shizuku.",
                                style = MaterialTheme.typography.bodyMedium
                            )
                            if (!accessibilityOn) {
                                Spacer(Modifier.height(Tokens.Space3))
                                Text("For typing from your PC, also turn on Baton's accessibility service.",
                                    style = MaterialTheme.typography.bodyMedium)
                                Spacer(Modifier.height(Tokens.Space2))
                                FilledTonalButton(onClick = { ShizukuAccess.enableAccessibility(context) }) { Text("Turn on") }
                            }
                            Spacer(Modifier.height(Tokens.Space3))
                            Button(onClick = onClose, modifier = Modifier.fillMaxWidth()) { Text("Done") }
                        }
                    }
                }
            }
        }
    }
}

private enum class Step { Install, DeveloperOptions, PairAndStart, Allow, Done }

private fun currentStep(state: ShizukuAccess.State, developerOptions: Boolean): Step = when (state) {
    ShizukuAccess.State.NotInstalled -> Step.Install
    ShizukuAccess.State.NotRunning -> if (developerOptions) Step.PairAndStart else Step.DeveloperOptions
    ShizukuAccess.State.NotAllowed -> Step.Allow
    ShizukuAccess.State.Ready -> Step.Done
}

@Composable
private fun StepCard(number: Int, title: String, current: Step, step: Step, content: @Composable () -> Unit) {
    val done = current.ordinal > step.ordinal
    val active = current == step
    Card(shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Tokens.Space4)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier.size(32.dp).clip(CircleShape).background(when {
                        done -> Color(0x3322C55E)
                        active -> MaterialTheme.colorScheme.primary
                        else -> MaterialTheme.colorScheme.surfaceVariant
                    }),
                    contentAlignment = Alignment.Center
                ) {
                    if (done) {
                        Icon(Icons.Rounded.Check, contentDescription = "Done", tint = Color(0xFF16A34A), modifier = Modifier.size(20.dp))
                    } else {
                        Text("$number", color = if (active) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
                            style = MaterialTheme.typography.labelLarge)
                    }
                }
                Spacer(Modifier.width(Tokens.Space3))
                Text(title, style = MaterialTheme.typography.titleMedium,
                    color = if (active || done) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (active) {
                Spacer(Modifier.height(Tokens.Space3))
                Column(verticalArrangement = Arrangement.spacedBy(Tokens.Space2)) { content() }
            }
        }
    }
}

@Composable
private fun Instruction(text: String) {
    Text(text, style = MaterialTheme.typography.bodyMedium)
}

@Composable
private fun Numbered(number: Int, text: String) {
    Row {
        Text("$number.", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold,
            modifier = Modifier.width(24.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
private fun Action(label: String, onClick: () -> Unit) {
    FilledTonalButton(onClick = onClick) { Text(label) }
}

private fun developerOptionsOn(context: Context): Boolean =
    Settings.Global.getInt(context.contentResolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, 0) == 1

private fun open(context: Context, intent: Intent): Boolean = try {
    context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    true
} catch (_: ActivityNotFoundException) {
    false
}

private fun openStore(context: Context) {
    val app = "moe.shizuku.privileged.api"
    if (!open(context, Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=$app")))) {
        open(context, Intent(Intent.ACTION_VIEW, Uri.parse("https://play.google.com/store/apps/details?id=$app")))
    }
}

/** Developer options, scrolled to Wireless debugging (the way Shizuku itself links there). */
private fun openWirelessDebugging(context: Context) {
    open(context, Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
        .putExtra(":settings:fragment_args_key", "toggle_adb_wireless"))
}
