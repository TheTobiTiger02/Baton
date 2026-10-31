package dev.baton.android.handoff

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.provider.ContactsContract
import android.view.accessibility.AccessibilityNodeInfo

/**
 * What an app shows, as a link another device can open: the place or route in Google Maps, the
 * chat in WhatsApp. Read from the screen by the accessibility service, like a browser's address,
 * and offered the same way ([BrowserPages]). Apps change their screens, so each reader looks for
 * labels that have stayed put for years and gives up quietly when they're gone.
 */
object AppPages {
    const val MAPS = "com.google.android.apps.maps"
    const val WHATSAPP = "com.whatsapp"
    val packages = setOf(MAPS, WHATSAPP)

    /** A link and a title for what [root] shows, or null when there is nothing to carry over. */
    fun read(context: Context, packageName: String, root: AccessibilityNodeInfo): Pair<String, String>? = when (packageName) {
        MAPS -> maps(root)
        WHATSAPP -> whatsApp(context, root)
        else -> null
    }

    // ---- Google Maps ----

    private const val MAPS_ID = "$MAPS:id/"
    private const val NOTHING_SEARCHED = "Search here"

    private fun maps(root: AccessibilityNodeInfo): Pair<String, String>? {
        val labels = labels(root)
        // A route: the start and destination fields say what they are.
        val destination = labels.firstNotNullOfOrNull { it.removePrefixOrNull("Destination, ") }
        if (destination != null) {
            val start = labels.firstNotNullOfOrNull { it.removePrefixOrNull("Start location, ") }
            val mode = labels.firstOrNull { it.selected && it.text.contains(" mode: ") }?.text?.substringBefore(" mode:")
            return MapsLinks.directions(start, destination, mode) to "Directions to $destination"
        }
        // A place: its card offers directions to it, by name and address.
        labels.firstNotNullOfOrNull { it.removePrefixOrNull("Directions to ") }?.let { place ->
            return MapsLinks.search(place) to place.substringBefore(", ")
        }
        // A search, while it isn't being typed.
        val box = root.findAccessibilityNodeInfosByViewId("${MAPS_ID}search_omnibox_text_box").firstOrNull() ?: return null
        val query = box.text?.toString()?.trim()
        if (box.isFocused || query.isNullOrEmpty() || query == NOTHING_SEARCHED) return null
        return MapsLinks.search(query) to query
    }

    // ---- WhatsApp ----

    private const val WHATSAPP_ID = "$WHATSAPP:id/"

    /** The open chat, when its number is known: shown as the title, or a contact's on this phone. */
    private fun whatsApp(context: Context, root: AccessibilityNodeInfo): Pair<String, String>? {
        val name = root.findAccessibilityNodeInfosByViewId("${WHATSAPP_ID}conversation_contact_name").firstOrNull()
            ?.text?.toString()?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        val number = ChatLinks.numberIn(name) ?: contactNumber(context, name) ?: return null
        val draft = root.findAccessibilityNodeInfosByViewId("${WHATSAPP_ID}entry").firstOrNull()
            ?.takeUnless { it.isShowingHintText }?.text?.toString()
        return ChatLinks.whatsApp(number, draft) to "Chat with $name"
    }

    /** The number saved for a contact with exactly this name; needs the contacts permission. */
    private fun contactNumber(context: Context, name: String): String? {
        if (context.checkSelfPermission(Manifest.permission.READ_CONTACTS) != PackageManager.PERMISSION_GRANTED) return null
        return runCatching {
            context.contentResolver.query(
                ContactsContract.CommonDataKinds.Phone.CONTENT_URI,
                arrayOf(ContactsContract.CommonDataKinds.Phone.NORMALIZED_NUMBER, ContactsContract.CommonDataKinds.Phone.NUMBER),
                "${ContactsContract.CommonDataKinds.Phone.DISPLAY_NAME} = ?", arrayOf(name), null
            )?.use { cursor ->
                if (cursor.moveToFirst()) ChatLinks.numberIn(cursor.getString(0) ?: cursor.getString(1).orEmpty()) else null
            }
        }.getOrNull()
    }

    // ---- The screen ----

    private class Label(val text: String, val selected: Boolean) {
        fun removePrefixOrNull(prefix: String) = if (text.startsWith(prefix)) text.removePrefix(prefix).trim().takeIf { it.isNotEmpty() } else null
    }

    /** Every text and description on screen, at most a few hundred, so a busy screen stays cheap. */
    private fun labels(root: AccessibilityNodeInfo): List<Label> {
        val found = mutableListOf<Label>()
        val pending = ArrayDeque<AccessibilityNodeInfo>().apply { add(root) }
        var visited = 0
        while (pending.isNotEmpty() && visited++ < MAX_NODES) {
            val node = pending.removeFirst()
            listOfNotNull(node.contentDescription, node.text).forEach { found += Label(it.toString(), node.isSelected) }
            for (index in 0 until node.childCount) node.getChild(index)?.let(pending::add)
        }
        return found
    }

    private const val MAX_NODES = 600
}

/** A query or path part, encoded the way links expect ("%20", not "+"). */
private fun encode(text: String): String = java.net.URLEncoder.encode(text, Charsets.UTF_8).replace("+", "%20")

/** Google Maps links any device opens, in the app or a browser. */
object MapsLinks {
    fun search(query: String): String = "https://www.google.com/maps/search/?api=1&query=" + encode(query)

    /** [start] is left out when it's "Your location": the other device's location is the right start there. */
    fun directions(start: String?, destination: String, mode: String?): String = buildString {
        append("https://www.google.com/maps/dir/?api=1")
        if (start != null && start != "Your location") append("&origin=").append(encode(start))
        append("&destination=").append(encode(destination))
        travelMode(mode)?.let { append("&travelmode=").append(it) }
    }

    private fun travelMode(mode: String?): String? = when (mode?.lowercase()) {
        "driving" -> "driving"
        "transit" -> "transit"
        "walking" -> "walking"
        "bicycling", "cycling" -> "bicycling"
        else -> null
    }
}

/** Links that open a chat in the messenger's own app on the PC. */
object ChatLinks {
    /** The digits of an international number ("+49 151 234…"), or null when [text] isn't one. */
    fun numberIn(text: String): String? {
        val trimmed = text.trim()
        if (!trimmed.startsWith("+") && !trimmed.startsWith("00")) return null
        if (trimmed.any { !it.isDigit() && it !in "+ -()/." }) return null
        return trimmed.filter(Char::isDigit).removePrefix(if (trimmed.startsWith("00")) "00" else "").takeIf { it.length in 7..15 }
    }

    /** `wa.me`, with what was typed but not sent, which WhatsApp puts in the box again. */
    fun whatsApp(number: String, draft: String?): String =
        "https://wa.me/$number" + (draft?.takeIf { it.isNotBlank() }?.let { "?text=" + encode(it) } ?: "")
}
