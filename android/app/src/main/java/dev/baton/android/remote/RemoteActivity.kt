package dev.baton.android.remote

import android.os.Bundle
import android.view.KeyEvent
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Bedtime
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.Mic
import androidx.compose.material.icons.rounded.MicOff
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material.icons.rounded.SkipPrevious
import androidx.compose.material.icons.rounded.VolumeOff
import androidx.compose.material.icons.rounded.VolumeUp
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.baton.android.link.Link
import dev.baton.android.protocol.PcControlActions
import dev.baton.android.stream.ControlMessageCodec
import dev.baton.android.ui.BatonTheme
import dev.baton.android.ui.Tokens
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * This phone as the PC's remote: a touchpad and keyboard for the whole desktop, the PC's volume,
 * media keys and microphone, and lock, sleep and shut down. On a tablet the controls and the
 * touchpad sit side by side.
 */
class RemoteActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContent { BatonTheme { Surface(color = MaterialTheme.colorScheme.background) { RemoteScreen(onBack = ::finish) } } }
    }
}

@Composable
private fun RemoteScreen(onBack: () -> Unit) {
    val status by Link.status.collectAsStateWithLifecycle()
    val wide = LocalConfiguration.current.screenWidthDp >= 600
    LaunchedEffect(status.isReady) { if (status.isReady) PcControl.send(PcControlActions.STATE) }

    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(Tokens.Space4)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back") }
            Text("Remote", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.weight(1f))
            if (!status.isReady) Text("Not connected", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
        }
        Spacer(Modifier.height(Tokens.Space3))
        if (wide) {
            Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(Tokens.Space4)) {
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) { Controls() }
                Column(Modifier.weight(1.4f)) { Touchpad(Modifier.weight(1f)); Spacer(Modifier.height(Tokens.Space3)); Keyboard() }
            }
        } else {
            Controls()
            Spacer(Modifier.height(Tokens.Space3))
            Touchpad(Modifier.weight(1f))
            Spacer(Modifier.height(Tokens.Space3))
            Keyboard()
        }
    }
}

@Composable
private fun Controls() {
    val state by PcControl.state.collectAsStateWithLifecycle()
    var volume by remember { mutableFloatStateOf(0.5f) }
    var dragging by remember { mutableStateOf(false) }
    var confirmShutdown by remember { mutableStateOf(false) }
    LaunchedEffect(state) { if (!dragging) state?.volume?.let { volume = it.toFloat() } }

    Card(shape = RoundedCornerShape(Tokens.CardRadius), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh)) {
        Column(Modifier.padding(Tokens.Space4)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                val muted = state?.muted == true
                IconButton(onClick = { PcControl.send(PcControlActions.MUTE, if (muted) 0.0 else 1.0) }) {
                    Icon(if (muted) Icons.Rounded.VolumeOff else Icons.Rounded.VolumeUp, contentDescription = if (muted) "Unmute the PC" else "Mute the PC")
                }
                Slider(
                    value = volume,
                    onValueChange = {
                        dragging = true
                        volume = it
                        PcControl.sendVolume(it.toDouble())
                    },
                    onValueChangeFinished = {
                        dragging = false
                        PcControl.send(PcControlActions.VOLUME, volume.toDouble())
                    },
                    modifier = Modifier.weight(1f)
                )
                Text("${(volume * 100).roundToInt()}%", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.width(44.dp))
            }
            Row(horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                FilledTonalIconButton(onClick = { PcControl.send(PcControlActions.PREVIOUS) }) { Icon(Icons.Rounded.SkipPrevious, contentDescription = "Previous") }
                FilledTonalIconButton(onClick = { PcControl.send(PcControlActions.PLAY_PAUSE) }) { Icon(Icons.Rounded.Pause, contentDescription = "Play or pause") }
                FilledTonalIconButton(onClick = { PcControl.send(PcControlActions.NEXT) }) { Icon(Icons.Rounded.SkipNext, contentDescription = "Next") }
                val micMuted = state?.micMuted == true
                FilledTonalIconButton(onClick = { PcControl.send(PcControlActions.MIC_MUTE, if (micMuted) 0.0 else 1.0) }) {
                    Icon(if (micMuted) Icons.Rounded.MicOff else Icons.Rounded.Mic, contentDescription = if (micMuted) "Turn the PC's microphone on" else "Mute the PC's microphone")
                }
            }
            Spacer(Modifier.height(Tokens.Space2))
            Row(horizontalArrangement = Arrangement.spacedBy(Tokens.Space2), modifier = Modifier.fillMaxWidth()) {
                OutlinedButton(onClick = { PcControl.send(PcControlActions.LOCK) }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Rounded.Lock, contentDescription = null); Spacer(Modifier.width(Tokens.Space1)); Text("Lock")
                }
                OutlinedButton(onClick = { PcControl.send(PcControlActions.SLEEP) }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Rounded.Bedtime, contentDescription = null); Spacer(Modifier.width(Tokens.Space1)); Text("Sleep")
                }
                OutlinedButton(onClick = { confirmShutdown = true }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Rounded.PowerSettingsNew, contentDescription = null); Spacer(Modifier.width(Tokens.Space1)); Text("Off")
                }
            }
        }
    }

    if (confirmShutdown) {
        AlertDialog(
            onDismissRequest = { confirmShutdown = false },
            title = { Text("Shut down your PC?") },
            text = { Text("Apps on the PC close; anything unsaved may be lost.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmShutdown = false
                    PcControl.send(PcControlActions.SHUTDOWN)
                }) { Text("Shut down", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmShutdown = false }) { Text("Cancel") } }
        )
    }
}

/**
 * One finger moves the pointer (faster the faster it moves) and taps to click; two fingers scroll
 * and tap to right-click.
 */
@Composable
private fun Touchpad(modifier: Modifier) {
    Box(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(Tokens.CardRadius))
            .background(MaterialTheme.colorScheme.surfaceContainerHighest)
            .pointerInput(Unit) {
                awaitEachGesture {
                    val first = awaitFirstDown()
                    val startedAt = first.uptimeMillis
                    var fingers = 1
                    var travel = 0f
                    var carry = Offset.Zero
                    var scroll = 0f
                    while (true) {
                        val event = awaitPointerEvent()
                        val pressed = event.changes.filter { it.pressed }
                        fingers = maxOf(fingers, pressed.size)
                        if (pressed.size == 1) {
                            val change = pressed[0]
                            val delta = change.position - change.previousPosition
                            travel += delta.getDistance()
                            // Acceleration: slow moves are precise, quick ones cross the screen.
                            val speed = 1.4f + minOf(delta.getDistance() / 12f, 2.6f)
                            carry += delta * speed
                            val dx = carry.x.toInt()
                            val dy = carry.y.toInt()
                            if (dx != 0 || dy != 0) {
                                PcControl.input(ControlMessageCodec.mouseMove(dx, dy))
                                carry -= Offset(dx.toFloat(), dy.toFloat())
                            }
                        } else if (pressed.size >= 2) {
                            val dy = pressed.map { (it.position - it.previousPosition).y }.average().toFloat()
                            travel += abs(dy)
                            scroll += dy
                            val notches = (scroll / SCROLL_STEP).toInt()
                            if (notches != 0) {
                                PcControl.input(ControlMessageCodec.scroll(0, 0, 0, 0, 0, notches))
                                scroll -= notches * SCROLL_STEP
                            }
                        }
                        event.changes.forEach { it.consume() }
                        if (event.changes.none { it.pressed }) break
                    }
                    val lasted = (currentEvent.changes.firstOrNull()?.uptimeMillis ?: startedAt) - startedAt
                    if (travel < TAP_SLOP && lasted < TAP_MS) {
                        val button = if (fingers >= 2) ControlMessageCodec.BUTTON_RIGHT else ControlMessageCodec.BUTTON_LEFT
                        PcControl.input(ControlMessageCodec.mouseButton(button, ControlMessageCodec.KEY_DOWN))
                        PcControl.input(ControlMessageCodec.mouseButton(button, ControlMessageCodec.KEY_UP))
                    }
                }
            },
        contentAlignment = Alignment.Center
    ) {
        Text("Touchpad · tap to click, two fingers to scroll or right-click", style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(Tokens.Space4))
    }
}

/** What is typed here goes to the PC as it is typed; ⌫ deletes there, Enter presses Enter. */
@Composable
private fun Keyboard() {
    var text by remember { mutableStateOf("") }
    OutlinedTextField(
        value = text,
        onValueChange = { next ->
            when {
                next.startsWith(text) && next.length > text.length -> PcControl.input(ControlMessageCodec.text(next.substring(text.length)))
                text.startsWith(next) -> repeat(text.length - next.length) { PcControl.key(KeyEvent.KEYCODE_DEL) }
                else -> {
                    repeat(text.length) { PcControl.key(KeyEvent.KEYCODE_DEL) }
                    PcControl.input(ControlMessageCodec.text(next))
                }
            }
            // Short, so the phone's keyboard never has much to correct.
            text = next.takeLast(40)
        },
        placeholder = { Text("Type on your PC") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send, autoCorrectEnabled = false),
        keyboardActions = KeyboardActions(onSend = {
            PcControl.key(KeyEvent.KEYCODE_ENTER)
            text = ""
        }),
        modifier = Modifier.fillMaxWidth()
    )
}

private const val SCROLL_STEP = 40f
private const val TAP_SLOP = 18f
private const val TAP_MS = 250L
