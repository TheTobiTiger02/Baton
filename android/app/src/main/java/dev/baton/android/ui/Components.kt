package dev.baton.android.ui

import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.DesktopWindows
import androidx.compose.material.icons.rounded.Language
import androidx.compose.material.icons.rounded.Movie
import androidx.compose.material.icons.rounded.MusicNote
import androidx.compose.material.icons.automirrored.rounded.VolumeOff
import androidx.compose.material.icons.automirrored.rounded.VolumeUp
import androidx.compose.material.icons.rounded.Forward10
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Replay10
import androidx.compose.material3.Button
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.runtime.mutableStateOf
import dev.baton.android.protocol.MediaActions
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import dev.baton.android.link.LinkPhase
import dev.baton.android.link.LinkStatus
import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.ActivityKind
import kotlinx.coroutines.delay

/** Ticks once a second while composed, so progress bars of playing media move. */
@Composable
fun rememberNow(): Long {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(1_000)
            now = System.currentTimeMillis()
        }
    }
    return now
}

@Composable
fun ActivityArt(activity: Activity, size: Dp) {
    val bitmap = remember(activity.artworkJpegBase64) { decode(activity.artworkJpegBase64) }
    Box(
        modifier = Modifier
            .size(size)
            .clip(RoundedCornerShape(Tokens.ArtRadius))
            .background(Tokens.Brand),
        contentAlignment = Alignment.Center
    ) {
        if (bitmap != null) {
            Image(bitmap, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.size(size))
        } else {
            Icon(glyph(activity), contentDescription = null, tint = Color.White, modifier = Modifier.size(size / 2.4f))
        }
    }
}

private fun glyph(activity: Activity): ImageVector = when {
    activity.kind == ActivityKind.WebPage -> Icons.Rounded.Language
    activity.kind == ActivityKind.WindowStream -> Icons.Rounded.DesktopWindows
    activity.content?.provider in setOf("spotify", "youtubemusic") -> Icons.Rounded.MusicNote
    else -> Icons.Rounded.Movie
}

private fun decode(base64: String?): ImageBitmap? = base64?.let {
    runCatching {
        val bytes = Base64.decode(it, Base64.DEFAULT)
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
    }.getOrNull()
}

private fun formatTime(ms: Long): String {
    val total = ms / 1000
    val hours = total / 3600
    val minutes = (total % 3600) / 60
    val seconds = total % 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, seconds) else "%d:%02d".format(minutes, seconds)
}

/** Sends a remote command (a [dev.baton.android.protocol.MediaActions] action) for a card's activity. */
fun interface RemoteControl {
    fun send(action: String, positionMs: Long?, volume: Double?)
}

/** A second, quieter action on a card, e.g. "Stream instead". */
data class CardAction(val label: String, val icon: ImageVector, val onClick: () -> Unit)

/**
 * An activity as a card: artwork, title, where it's running, progress, and one clear action.
 * [hero] makes it the big card at the top of the home screen. With [remote], media on another
 * device can be played, paused, seeked and turned up or down from here, without moving it.
 */
@Composable
fun ActivityCard(
    activity: Activity,
    actionLabel: String,
    actionIcon: ImageVector,
    onAction: () -> Unit,
    hero: Boolean = false,
    enabled: Boolean = true,
    remote: RemoteControl? = null,
    secondaryAction: CardAction? = null,
    choiceLabel: String? = null,
    onChangeChoice: (() -> Unit)? = null
) {
    val now = rememberNow()
    val playback = activity.playback
    Card(
        shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(
            containerColor = if (hero) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh,
            contentColor = if (hero) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurface
        ),
        modifier = Modifier.fillMaxWidth().animateContentSize()
    ) {
        // Secondary text follows the card's own content colour, so it reads on either container.
        val secondary = LocalContentColor.current.copy(alpha = 0.72f)
        Column(Modifier.padding(if (hero) Tokens.Space5 else Tokens.Space4)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ActivityArt(activity, if (hero) 88.dp else 56.dp)
                Spacer(Modifier.width(Tokens.Space4))
                Column(Modifier.weight(1f)) {
                    Text(
                        activity.title,
                        style = if (hero) MaterialTheme.typography.titleLarge else MaterialTheme.typography.titleMedium,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                    activity.subtitle?.let {
                        Text(it, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis,
                            color = secondary)
                    }
                    Spacer(Modifier.height(Tokens.Space1))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        if (playback?.playing == true) {
                            Box(Modifier.size(7.dp).clip(CircleShape).background(Color(0xFF22C55E)))
                            Spacer(Modifier.width(6.dp))
                        }
                        Text(
                            when {
                                playback?.playing == true -> "Playing in ${activity.app.name}"
                                playback != null -> "Paused in ${activity.app.name}"
                                else -> activity.app.name
                            },
                            style = MaterialTheme.typography.labelMedium,
                            color = secondary
                        )
                    }
                }
            }

            if (playback != null && remote != null && enabled) {
                RemoteControls(activity, playback, now, remote, secondary)
            } else if (playback != null && playback.durationMs > 0) {
                val position = playback.positionAt(now)
                Spacer(Modifier.height(Tokens.Space3))
                LinearProgressIndicator(
                    progress = { (position.toFloat() / playback.durationMs).coerceIn(0f, 1f) },
                    strokeCap = StrokeCap.Round,
                    drawStopIndicator = {},
                    gapSize = 0.dp,
                    modifier = Modifier.fillMaxWidth().height(5.dp)
                )
                Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(formatTime(position), style = MaterialTheme.typography.labelSmall, color = secondary)
                    Text(formatTime(playback.durationMs), style = MaterialTheme.typography.labelSmall, color = secondary)
                }
            }

            Spacer(Modifier.height(Tokens.Space3))
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (hero) {
                    Button(onClick = onAction, enabled = enabled, modifier = Modifier.weight(1f).height(52.dp)) {
                        Icon(actionIcon, contentDescription = null)
                        Spacer(Modifier.width(Tokens.Space2))
                        Text(actionLabel, style = MaterialTheme.typography.titleMedium)
                    }
                } else {
                    FilledTonalButton(onClick = onAction, enabled = enabled, modifier = Modifier.weight(1f)) {
                        Icon(actionIcon, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(Tokens.Space2))
                        Text(actionLabel)
                    }
                }
                secondaryAction?.let { extra ->
                    Spacer(Modifier.width(Tokens.Space2))
                    OutlinedButton(onClick = extra.onClick, enabled = enabled, modifier = if (hero) Modifier.height(52.dp) else Modifier) {
                        Icon(extra.icon, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(extra.label)
                    }
                }
            }
            if (onChangeChoice != null) {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(top = 2.dp)) {
                    Text(if (choiceLabel != null) "Opens in $choiceLabel" else "Opens the best way",
                        style = MaterialTheme.typography.labelMedium, color = secondary, modifier = Modifier.weight(1f))
                    androidx.compose.material3.TextButton(onClick = onChangeChoice, enabled = enabled) {
                        Text(if (choiceLabel != null) "Change" else "Choose app")
                    }
                }
            }
        }
    }
}

/** Seek bar, ±10 s, play/pause and the app's volume, for media playing on another device. */
@Composable
private fun RemoteControls(activity: Activity, playback: dev.baton.android.protocol.Playback, now: Long, remote: RemoteControl, secondary: Color) {
    val position = playback.positionAt(now)
    Spacer(Modifier.height(Tokens.Space2))
    if (playback.durationMs > 0) {
        var seeking by remember(activity.id) { mutableStateOf<Float?>(null) }
        Slider(
            value = seeking ?: (position.toFloat() / playback.durationMs).coerceIn(0f, 1f),
            onValueChange = { seeking = it },
            onValueChangeFinished = {
                seeking?.let { remote.send(MediaActions.SEEK, (it * playback.durationMs).toLong(), null) }
                seeking = null
            },
            modifier = Modifier.fillMaxWidth().height(28.dp)
        )
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(formatTime(seeking?.let { (it * playback.durationMs).toLong() } ?: position), style = MaterialTheme.typography.labelSmall, color = secondary)
            Text(formatTime(playback.durationMs), style = MaterialTheme.typography.labelSmall, color = secondary)
        }
    }
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { remote.send(MediaActions.SKIP, -10_000, null) }) {
            Icon(Icons.Rounded.Replay10, contentDescription = "Back 10 seconds")
        }
        Spacer(Modifier.width(Tokens.Space3))
        FilledIconButton(onClick = { remote.send(MediaActions.TOGGLE, null, null) }, modifier = Modifier.size(52.dp)) {
            Icon(if (playback.playing) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                contentDescription = if (playback.playing) "Pause" else "Play", modifier = Modifier.size(30.dp))
        }
        Spacer(Modifier.width(Tokens.Space3))
        IconButton(onClick = { remote.send(MediaActions.SKIP, 10_000, null) }) {
            Icon(Icons.Rounded.Forward10, contentDescription = "Forward 10 seconds")
        }
    }
    activity.volume?.let { volume ->
        var dragging by remember(activity.id) { mutableStateOf<Float?>(null) }
        val lastSent = remember { longArrayOf(0) }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(if ((dragging ?: volume.toFloat()) == 0f) Icons.AutoMirrored.Rounded.VolumeOff else Icons.AutoMirrored.Rounded.VolumeUp,
                contentDescription = "Volume", tint = secondary, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(Tokens.Space2))
            Slider(
                value = dragging ?: volume.toFloat(),
                onValueChange = {
                    dragging = it
                    // Follow the finger, but not with a message per pixel.
                    if (System.currentTimeMillis() - lastSent[0] > 80) {
                        lastSent[0] = System.currentTimeMillis()
                        remote.send(MediaActions.VOLUME, null, it.toDouble())
                    }
                },
                onValueChangeFinished = {
                    dragging?.let { remote.send(MediaActions.VOLUME, null, it.toDouble()) }
                    dragging = null
                },
                modifier = Modifier.weight(1f).height(28.dp)
            )
        }
    }
}

@Composable
fun StatusPill(status: LinkStatus, pcName: String?) {
    val (color, text) = when (status.phase) {
        LinkPhase.Ready -> Color(0xFF22C55E) to "Connected to ${pcName ?: "your PC"}"
        LinkPhase.Searching, LinkPhase.Connecting, LinkPhase.Pairing -> Color(0xFFF59E0B) to status.detail.ifBlank { "Connecting…" }
        LinkPhase.Offline -> Color(0xFFEF4444) to status.detail.ifBlank { "Offline" }
        LinkPhase.Unpaired -> MaterialTheme.colorScheme.outline to "Not paired"
    }
    Surface(shape = RoundedCornerShape(50), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).clip(CircleShape).background(color))
            Spacer(Modifier.width(8.dp))
            Text(text, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
fun SectionTitle(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(top = Tokens.Space5, bottom = Tokens.Space2, start = Tokens.Space1)
    )
}

@Composable
fun EmptyCard(text: String) {
    Card(
        shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
        modifier = Modifier.fillMaxWidth()
    ) {
        Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(Tokens.Space5))
    }
}

/** A setup step: what it is for, whether it's done, and the button that does it. */
@Composable
fun PermissionCard(icon: ImageVector, title: String, text: String, granted: Boolean, actionLabel: String, onAction: () -> Unit) {
    Card(
        shape = RoundedCornerShape(Tokens.CardRadius),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(Modifier.padding(Tokens.Space4), verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier.size(40.dp).clip(CircleShape)
                    .background(if (granted) Color(0x3322C55E) else MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center
            ) {
                Icon(if (granted) Icons.Rounded.Check else icon, contentDescription = null,
                    tint = if (granted) Color(0xFF16A34A) else MaterialTheme.colorScheme.onPrimaryContainer)
            }
            Spacer(Modifier.width(Tokens.Space4))
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (!granted) {
                Spacer(Modifier.width(Tokens.Space2))
                FilledTonalButton(onClick = onAction) { Text(actionLabel) }
            }
        }
    }
}
