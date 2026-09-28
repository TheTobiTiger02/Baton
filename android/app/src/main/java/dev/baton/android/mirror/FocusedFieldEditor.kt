package dev.baton.android.mirror

import android.accessibilityservice.AccessibilityService
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.view.KeyEvent
import android.view.accessibility.AccessibilityNodeInfo

/**
 * Types into the focused text field through the accessibility service, with no keyboard switch.
 *
 * This is the fallback when Smartphone Link's own keyboard is not the active one. It edits the
 * field the way a keyboard would - insert at the cursor, delete around it, move it - using the
 * accessibility actions every standard text field supports. Custom editors that draw their own text
 * (some games, some web editors) do not expose these actions; for them the keyboard is the answer.
 */
internal class FocusedFieldEditor(private val service: AccessibilityService) {

    /** Inserts [value] at the cursor, replacing any selection. */
    fun insert(value: String): Boolean = edit { text, start, end ->
        Edit(text.replaceRange(start, end, value), start + value.length)
    }

    /**
     * Applies an editing key. Returns false for keys this path cannot express, so the caller can
     * report them instead of silently dropping them.
     */
    fun key(keyCode: Int, metaState: Int): Boolean {
        val control = metaState and KeyEvent.META_CTRL_ON != 0

        if (control) {
            return when (keyCode) {
                KeyEvent.KEYCODE_A -> select { text, _, _ -> 0 to text.length }
                KeyEvent.KEYCODE_C -> shareSelection().let { perform(AccessibilityNodeInfo.ACTION_COPY) }
                KeyEvent.KEYCODE_X -> shareSelection().let { perform(AccessibilityNodeInfo.ACTION_CUT) }
                else -> false
            }
        }

        return when (keyCode) {
            KeyEvent.KEYCODE_DEL -> edit { text, start, end ->
                when {
                    start != end -> Edit(text.removeRange(start, end), start)
                    start > 0 -> Edit(text.removeRange(start - 1, start), start - 1)
                    else -> null
                }
            }

            KeyEvent.KEYCODE_FORWARD_DEL -> edit { text, start, end ->
                when {
                    start != end -> Edit(text.removeRange(start, end), start)
                    end < text.length -> Edit(text.removeRange(start, start + 1), start)
                    else -> null
                }
            }

            KeyEvent.KEYCODE_ENTER -> enter()
            KeyEvent.KEYCODE_DPAD_LEFT -> select { _, start, end -> caret(if (start != end) start else start - 1) }
            KeyEvent.KEYCODE_DPAD_RIGHT -> select { text, start, end -> caret(if (start != end) end else (end + 1).coerceAtMost(text.length)) }
            KeyEvent.KEYCODE_MOVE_HOME -> select { _, _, _ -> caret(0) }
            KeyEvent.KEYCODE_MOVE_END -> select { text, _, _ -> caret(text.length) }
            else -> false
        }
    }

    fun paste(): Boolean = perform(AccessibilityNodeInfo.ACTION_PASTE)

    /**
     * Copying in the mirror: the selection goes to the PC's clipboard too. Read here, before the
     * copy, because Baton can't read the phone's clipboard while it isn't the app in front.
     */
    private fun shareSelection() {
        val node = focusedField() ?: return
        try {
            if (node.isPassword) return
            val (text, start, end) = currentState(node)
            if (start < end) dev.baton.android.stream.ClipboardSync.copied(text.substring(start, end))
        } finally {
            node.recycleSafely()
        }
    }

    private data class Edit(val text: String, val caret: Int)

    private fun caret(position: Int): Pair<Int, Int> = position.coerceAtLeast(0).let { it to it }

    private fun enter(): Boolean {
        val node = focusedField() ?: return false
        val submit = try {
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && !node.isMultiLine
        } finally {
            node.recycleSafely()
        }

        // Submit (search, send) where the field asks for it; a newline in multi-line fields.
        return if (submit) {
            perform(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        } else {
            insert("\n")
        }
    }

    private fun perform(action: Int): Boolean {
        val node = focusedField() ?: return false
        return try {
            node.performAction(action)
        } finally {
            node.recycleSafely()
        }
    }

    private fun edit(change: (text: String, start: Int, end: Int) -> Edit?): Boolean {
        val node = focusedField() ?: return false
        try {
            val (text, start, end) = currentState(node)
            val edit = change(text, start, end) ?: return true

            val setText = Bundle().apply {
                putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, edit.text)
            }
            if (!node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, setText)) {
                return false
            }

            // SET_TEXT puts the caret at the end; put it back where the edit happened.
            node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selection(edit.caret, edit.caret))
            lastEdit = LastEdit(AccessibilityNodeInfo(node), edit, SystemClock.uptimeMillis())
            return true
        } finally {
            node.recycleSafely()
        }
    }

    private fun select(range: (text: String, start: Int, end: Int) -> Pair<Int, Int>): Boolean {
        val node = focusedField() ?: return false
        lastEdit = null
        return try {
            val (text, start, end) = currentState(node)
            val (from, to) = range(text, start, end)
            node.performAction(
                AccessibilityNodeInfo.ACTION_SET_SELECTION,
                selection(from.coerceIn(0, text.length), to.coerceIn(0, text.length))
            )
        } finally {
            node.recycleSafely()
        }
    }

    private fun currentState(node: AccessibilityNodeInfo): Triple<String, Int, Int> {
        // The node is a snapshot that lags our own SET_TEXT by a moment; fast typing would read the
        // text from before the previous character and drop it. Right after an edit, trust the edit.
        lastEdit?.let { last ->
            if (last.field == node && SystemClock.uptimeMillis() - last.at < RECENT_EDIT_MS) {
                return Triple(last.edit.text, last.edit.caret, last.edit.caret)
            }
        }

        // An empty field reports its hint as its text; treating that as content would type
        // after the hint.
        val showingHint = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && node.isShowingHintText
        val text = if (showingHint) "" else node.text?.toString().orEmpty()

        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || end < 0) {
            start = text.length
            end = text.length
        }
        if (start > end) {
            val swap = start
            start = end
            end = swap
        }

        return Triple(text, start.coerceIn(0, text.length), end.coerceIn(0, text.length))
    }

    private fun selection(start: Int, end: Int) = Bundle().apply {
        putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, start)
        putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, end)
    }

    private fun focusedField(): AccessibilityNodeInfo? {
        val root = service.rootInActiveWindow ?: return null
        val focused = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        if (focused !== root) root.recycleSafely()
        return focused?.takeIf { it.isEditable }
    }

    private class LastEdit(val field: AccessibilityNodeInfo, val edit: Edit, val at: Long)

    private companion object {
        const val RECENT_EDIT_MS = 800L
        var lastEdit: LastEdit? = null
    }

    @Suppress("DEPRECATION")
    private fun AccessibilityNodeInfo.recycleSafely() {
        // Recycling is a no-op from API 33; before that it returns the node to the pool.
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) runCatching { recycle() }
    }
}
