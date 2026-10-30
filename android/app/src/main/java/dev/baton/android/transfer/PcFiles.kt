package dev.baton.android.transfer

import android.os.Bundle
import android.text.format.Formatter
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.InsertDriveFile
import androidx.compose.material.icons.rounded.Folder
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.baton.android.link.Link
import dev.baton.android.protocol.FileFetchPayload
import dev.baton.android.protocol.FolderEntry
import dev.baton.android.protocol.FolderListPayload
import dev.baton.android.protocol.FolderListingPayload
import dev.baton.android.protocol.MessageTypes
import dev.baton.android.protocol.Wire
import dev.baton.android.ui.BatonTheme
import dev.baton.android.ui.Tokens
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The folders the PC shares, read-only: listings over the session (`folder.list`), and a file
 * fetched (`file.fetch`) arrives as any file from the PC does, saved in Download/Baton and opened.
 */
object PcFiles {
    private val listingFlow = MutableStateFlow<FolderListingPayload?>(null)
    /** The answer to the latest [browse]; an older answer arriving late is dropped. */
    val listing: StateFlow<FolderListingPayload?> = listingFlow.asStateFlow()
    @Volatile private var asked: String? = null

    /** Asks for what is in [path], or for the shared folders when it is null. False when not connected. */
    fun browse(path: String?): Boolean {
        val requestId = Wire.newId()
        asked = requestId
        listingFlow.value = null
        return Link.send(MessageTypes.FOLDER_LIST, FolderListPayload(requestId, path))
    }

    fun fetch(entry: FolderEntry): Boolean = Link.send(MessageTypes.FILE_FETCH, FileFetchPayload(entry.path))

    fun onListing(listing: FolderListingPayload) {
        if (listing.requestId == asked) listingFlow.value = listing
    }
}

class PcFilesActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { BatonTheme { Surface(color = MaterialTheme.colorScheme.background) { PcFilesScreen(onExit = ::finish) } } }
    }
}

@Composable
private fun PcFilesScreen(onExit: () -> Unit) {
    val context = LocalContext.current
    val status by Link.status.collectAsStateWithLifecycle()
    val peers by Link.peers.collectAsStateWithLifecycle()
    val listing by PcFiles.listing.collectAsStateWithLifecycle()
    val pcName = peers.firstOrNull { it.kind == "pc" }?.name ?: "your PC"
    // The folders walked into, as (path, name); the shared folders themselves at the bottom.
    val trail = remember { mutableStateListOf<Pair<String?, String>>(null to "Files on $pcName") }
    val here = trail.last()

    LaunchedEffect(here.first, status.isReady) { if (status.isReady) PcFiles.browse(here.first) }
    fun up() {
        if (trail.size > 1) trail.removeAt(trail.lastIndex) else onExit()
    }
    BackHandler(onBack = ::up)

    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(Tokens.Space4)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = ::up) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back") }
            Text(here.second, style = MaterialTheme.typography.headlineSmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f))
        }
        Spacer(Modifier.height(Tokens.Space3))
        val shown = listing?.takeIf { it.path == here.first || here.first == null && it.path == null }
        when {
            !status.isReady -> Message("Not connected to $pcName.")
            shown == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            shown.error != null -> Message(shown.error)
            shown.entries.isEmpty() -> Message("This folder is empty.")
            else -> LazyColumn(Modifier.fillMaxSize()) {
                items(shown.entries, key = { it.path }) { entry ->
                    EntryRow(entry) {
                        if (entry.folder) {
                            trail.add(entry.path to entry.name)
                        } else if (PcFiles.fetch(entry)) {
                            Toast.makeText(context, "Downloading ${entry.name}…", Toast.LENGTH_SHORT).show()
                        }
                    }
                }
                if (shown.truncated) item {
                    Text("Only the first ${shown.entries.size} items are shown.", style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(Tokens.Space3))
                }
            }
        }
    }
}

@Composable
private fun EntryRow(entry: FolderEntry, onClick: () -> Unit) {
    val context = LocalContext.current
    val modified = remember(entry.modified) {
        runCatching { OffsetDateTime.parse(entry.modified).atZoneSameInstant(java.time.ZoneId.systemDefault())
            .format(DateTimeFormatter.ofLocalizedDateTime(FormatStyle.SHORT)) }.getOrNull()
    }
    val detail = listOfNotNull(if (entry.folder) null else Formatter.formatShortFileSize(context, entry.size), modified).joinToString(" · ")
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick, onClickLabel = if (entry.folder) "Open" else "Download")
            .padding(vertical = Tokens.Space3, horizontal = Tokens.Space2),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(if (entry.folder) Icons.Rounded.Folder else Icons.AutoMirrored.Rounded.InsertDriveFile, contentDescription = null,
            tint = if (entry.folder) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(Tokens.Space3))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Tokens.Space1)) {
            Text(entry.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (detail.isNotEmpty()) Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun Message(text: String) {
    Text(text, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(Tokens.Space3))
}
