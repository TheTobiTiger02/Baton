package dev.baton.android.ui

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Apps
import androidx.compose.material.icons.rounded.Cast
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.Language
import androidx.compose.material.icons.rounded.PlayCircle
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.baton.android.MainActivity
import dev.baton.android.apps.AppCatalog
import dev.baton.android.apps.Choices
import dev.baton.android.handoff.Launcher
import dev.baton.android.link.Link
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ChoiceKinds
import dev.baton.android.protocol.HandoffChoice
import dev.baton.android.protocol.InstalledApp
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * "Continue with…": which app something should continue in. Shown the first time an app is
 * continued (or when asked to change), answered once, and remembered from then on.
 */
object ChoicePrompt {
    data class Request(
        val activity: Activity,
        val targetName: String,
        val targetIsPhone: Boolean,
        val options: List<HandoffChoice>,
        val preselected: HandoffChoice,
        val answer: CompletableDeferred<HandoffChoice?>
    )

    private val requestFlow = MutableStateFlow<Request?>(null)
    val request: StateFlow<Request?> = requestFlow.asStateFlow()

    @Volatile var appVisible = false

    /** Shows the sheet (bringing Baton to the front if needed) and waits for the pick; null when dismissed. */
    suspend fun ask(context: Context, activity: Activity, targetDeviceId: String, options: List<HandoffChoice>, remembered: HandoffChoice?): HandoffChoice? {
        val targetIsPhone = targetDeviceId == Link.deviceId
        val targetName = if (targetIsPhone) "this phone" else Link.peers.value.firstOrNull { it.deviceId == targetDeviceId }?.name ?: "your PC"
        val preselected = options.firstOrNull { it.kind == remembered?.kind && it.appId == remembered.appId && it.url == remembered.url } ?: options.first()
        val answer = CompletableDeferred<HandoffChoice?>()
        requestFlow.value?.answer?.complete(null)
        requestFlow.value = Request(activity, targetName, targetIsPhone, options, preselected, answer)
        if (!appVisible) {
            Launcher.start(context, Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT), activity.title)
        }
        return try {
            answer.await()
        } finally {
            if (requestFlow.value?.answer === answer) requestFlow.value = null
        }
    }

    fun answer(choice: HandoffChoice?) {
        requestFlow.value?.answer?.complete(choice)
        requestFlow.value = null
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChoiceSheetHost() {
    val request by ChoicePrompt.request.collectAsState()
    val current = request ?: return
    var selected by remember(current) { mutableStateOf(current.preselected) }
    var remember by remember(current) { mutableStateOf(true) }
    ModalBottomSheet(onDismissRequest = { ChoicePrompt.answer(null) }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.padding(horizontal = Tokens.Space5).navigationBarsPadding()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ActivityArt(current.activity, 48.dp)
                Spacer(Modifier.width(Tokens.Space3))
                Column(Modifier.weight(1f)) {
                    Text("Continue ${current.activity.title}", style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("on ${current.targetName} with…", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            Spacer(Modifier.height(Tokens.Space3))
            current.options.forEach { option ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { selected = option }.padding(vertical = 6.dp)
                ) {
                    RadioButton(selected = option == selected, onClick = { selected = option })
                    OptionIcon(option, current.targetIsPhone)
                    Spacer(Modifier.width(Tokens.Space3))
                    Column(Modifier.weight(1f)) {
                        Text(option.label, style = MaterialTheme.typography.bodyLarge)
                        Text(describe(option, current.targetIsPhone), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(vertical = Tokens.Space2)) {
                Text("Always use this for ${subjectName(current.activity)}", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                Switch(checked = remember, onCheckedChange = { remember = it })
            }
            Button(onClick = { ChoicePrompt.answer(selected.copy(remember = remember)) }, modifier = Modifier.fillMaxWidth().height(52.dp)) {
                Text("Continue", style = MaterialTheme.typography.titleMedium)
            }
            Spacer(Modifier.height(Tokens.Space4))
        }
    }
}

@Composable
private fun OptionIcon(option: HandoffChoice, targetIsPhone: Boolean) {
    val context = LocalContext.current
    val icon = if (targetIsPhone && option.kind == ChoiceKinds.APP) option.appId?.let { remember(it) { AppCatalog.icon(context, it) } } else null
    Box(Modifier.size(36.dp), contentAlignment = Alignment.Center) {
        if (icon != null) {
            Image(icon.asImageBitmap(), contentDescription = null, modifier = Modifier.size(36.dp))
        } else {
            Icon(glyphFor(option), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        }
    }
}

private fun glyphFor(option: HandoffChoice): ImageVector = when (option.kind) {
    ChoiceKinds.WEB -> Icons.Rounded.Language
    ChoiceKinds.STREAM -> Icons.Rounded.Cast
    ChoiceKinds.DEFAULT -> Icons.Rounded.PlayCircle
    else -> Icons.Rounded.Computer
}

private fun describe(option: HandoffChoice, targetIsPhone: Boolean): String = when (option.kind) {
    ChoiceKinds.DEFAULT -> "Same content, same second"
    ChoiceKinds.WEB -> option.url?.removePrefix("https://")?.removeSuffix("/") ?: "Website"
    ChoiceKinds.STREAM -> if (targetIsPhone) "Watch and control the PC window live" else "Show the phone's screen and control it with mouse and keyboard"
    else -> if (targetIsPhone) "App on this phone" else "App on the PC"
}

/** Every app on this phone, to continue on the PC (not just the one in front). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppPickerSheet(onDismiss: () -> Unit, onPick: (InstalledApp) -> Unit) {
    val context = LocalContext.current
    val apps = remember { AppCatalog.apps(context) }
    var query by remember { mutableStateOf("") }
    val shown = remember(query, apps) { apps.filter { query.isBlank() || it.name.contains(query, ignoreCase = true) } }
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.padding(horizontal = Tokens.Space5)) {
            Text("Continue an app on your PC", style = MaterialTheme.typography.titleLarge)
            Spacer(Modifier.height(Tokens.Space3))
            OutlinedTextField(
                value = query, onValueChange = { query = it }, singleLine = true,
                leadingIcon = { Icon(Icons.Rounded.Search, contentDescription = null) },
                placeholder = { Text("Search apps") }, modifier = Modifier.fillMaxWidth()
            )
            Spacer(Modifier.height(Tokens.Space2))
            LazyColumn(Modifier.heightIn(max = 520.dp).navigationBarsPadding()) {
                items(shown, key = { it.id }) { app ->
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { onPick(app) }.padding(vertical = 8.dp, horizontal = 4.dp)
                    ) {
                        val icon = remember(app.id) { AppCatalog.icon(context, app.id) }
                        if (icon != null) Image(icon.asImageBitmap(), contentDescription = null, modifier = Modifier.size(40.dp))
                        else Icon(Icons.Rounded.Apps, contentDescription = null, modifier = Modifier.size(40.dp))
                        Spacer(Modifier.width(Tokens.Space3))
                        Text(app.name, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                    }
                }
            }
        }
    }
}

/** The remembered choices, each removable (the next handoff of that app asks again). */
@Composable
fun AppChoicesList() {
    val preferences by Choices.preferences.collectAsState()
    if (preferences.isEmpty()) {
        EmptyCard("No app choices yet. The first time you continue an app, Baton asks where it should open and remembers it.")
        return
    }
    Card(shape = RoundedCornerShape(Tokens.CardRadius), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh)) {
        Column(Modifier.padding(vertical = Tokens.Space2), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            preferences.values.sortedBy { it.sourceName.lowercase() }.forEach { preference ->
                val toPc = preference.key.endsWith("->windows")
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(start = Tokens.Space4, end = Tokens.Space2)) {
                    Column(Modifier.weight(1f)) {
                        Text(preference.sourceName, style = MaterialTheme.typography.bodyLarge)
                        Text("${if (toPc) "On the PC" else "On this phone"}: ${preference.choice.label}",
                            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    IconButton(onClick = { Choices.forget(preference.key) }) { Icon(Icons.Rounded.Close, contentDescription = "Forget") }
                }
            }
        }
    }
}

/** "YouTube" or "x.com" for something in a browser (choices are per site there), the app's name otherwise. */
fun subjectName(activity: Activity): String {
    val subject = dev.baton.android.protocol.ChoiceKeys.subject(activity)
    if (!subject.startsWith("site:")) return activity.app.name
    val site = subject.removePrefix("site:")
    return dev.baton.android.handoff.KnownApps.fromProvider(site)?.displayName ?: if (site == "web") "other web pages" else site
}

/** What a card's app opens in on the other side, for the card's "Opens in…" line. */
fun rememberedLabel(activity: Activity, sourceDeviceId: String, targetDeviceId: String): String? =
    Choices.remembered(activity, sourceDeviceId, targetDeviceId)?.label
