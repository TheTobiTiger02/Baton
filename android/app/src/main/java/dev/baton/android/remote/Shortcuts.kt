package dev.baton.android.remote

import android.content.Context
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import dev.baton.android.protocol.PcControlActions
import dev.baton.android.ui.Tokens
import org.json.JSONArray
import org.json.JSONObject

/** A button on the remote: a key combination to press on the PC, or a web page to open there. */
data class Shortcut(val label: String, val action: String, val value: String)

/** The remote's shortcuts, kept on this phone. A first look comes with a few that suit most PCs. */
object ShortcutStore {
    private const val PREFS = "baton_remote"
    private const val KEY = "shortcuts"

    private val defaults = listOf(
        Shortcut("Show desktop", PcControlActions.KEYS, "Win+D"),
        Shortcut("Switch window", PcControlActions.KEYS, "Win+Tab"),
        Shortcut("Close tab", PcControlActions.KEYS, "Ctrl+W")
    )

    fun load(context: Context): List<Shortcut> {
        val raw = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, null) ?: return defaults
        return runCatching {
            val array = JSONArray(raw)
            (0 until array.length()).map { index ->
                array.getJSONObject(index).let { Shortcut(it.getString("label"), it.getString("action"), it.getString("value")) }
            }
        }.getOrDefault(defaults)
    }

    fun save(context: Context, shortcuts: List<Shortcut>) {
        val array = JSONArray()
        shortcuts.forEach { array.put(JSONObject().put("label", it.label).put("action", it.action).put("value", it.value)) }
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY, array.toString()).apply()
    }
}

/** The shortcut buttons: tap to run on the PC, hold to remove, "+" to add one. */
@OptIn(ExperimentalLayoutApi::class, ExperimentalFoundationApi::class)
@Composable
fun Shortcuts() {
    val context = LocalContext.current
    var shortcuts by remember { mutableStateOf(ShortcutStore.load(context)) }
    var adding by remember { mutableStateOf(false) }
    var removing by remember { mutableStateOf<Shortcut?>(null) }

    fun update(next: List<Shortcut>) {
        shortcuts = next
        ShortcutStore.save(context, next)
    }

    Card(shape = RoundedCornerShape(Tokens.CardRadius), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Tokens.Space3)) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Tokens.Space2), verticalArrangement = Arrangement.spacedBy(Tokens.Space2)) {
                shortcuts.forEach { shortcut ->
                    Surface(
                        shape = RoundedCornerShape(50),
                        color = MaterialTheme.colorScheme.secondaryContainer,
                        modifier = Modifier.combinedClickable(
                            onClick = { PcControl.send(shortcut.action, text = shortcut.value) },
                            onLongClick = { removing = shortcut },
                            onClickLabel = "Run on the PC",
                            onLongClickLabel = "Remove"
                        )
                    ) {
                        Text(shortcut.label, style = MaterialTheme.typography.labelLarge,
                            modifier = Modifier.padding(horizontal = Tokens.Space3, vertical = Tokens.Space2))
                    }
                }
                TextButton(onClick = { adding = true }) { Text("+ Add") }
            }
        }
    }

    if (adding) AddShortcutDialog(onDismiss = { adding = false }) { shortcut ->
        adding = false
        update(shortcuts + shortcut)
    }
    removing?.let { shortcut ->
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text("Remove “${shortcut.label}”?") },
            confirmButton = { TextButton(onClick = { update(shortcuts - shortcut); removing = null }) { Text("Remove") } },
            dismissButton = { TextButton(onClick = { removing = null }) { Text("Cancel") } }
        )
    }
}

@Composable
private fun AddShortcutDialog(onDismiss: () -> Unit, onAdd: (Shortcut) -> Unit) {
    var label by remember { mutableStateOf("") }
    var value by remember { mutableStateOf("") }
    var action by remember { mutableStateOf(PcControlActions.KEYS) }
    val keys = action == PcControlActions.KEYS
    val valid = label.isNotBlank() && value.isNotBlank() &&
        (keys || value.startsWith("https://") || value.startsWith("http://"))

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("New shortcut") },
        text = {
            Column {
                Row(horizontalArrangement = Arrangement.spacedBy(Tokens.Space2)) {
                    FilterChip(selected = keys, onClick = { action = PcControlActions.KEYS }, label = { Text("Keys") })
                    FilterChip(selected = !keys, onClick = { action = PcControlActions.OPEN }, label = { Text("Website") })
                }
                Spacer(Modifier.height(Tokens.Space2))
                OutlinedTextField(value = label, onValueChange = { label = it }, label = { Text("Name") }, singleLine = true,
                    modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(Tokens.Space2))
                OutlinedTextField(
                    value = value, onValueChange = { value = it }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    label = { Text(if (keys) "Keys, e.g. Ctrl+Shift+T" else "Address, e.g. https://example.com") }
                )
            }
        },
        confirmButton = { TextButton(enabled = valid, onClick = { onAdd(Shortcut(label.trim(), action, value.trim())) }) { Text("Add") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } }
    )
}
