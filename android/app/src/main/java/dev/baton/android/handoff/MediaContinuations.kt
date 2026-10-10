package dev.baton.android.handoff

import dev.baton.android.protocol.Activity

/** Session-only return context for the actual app a PC browser handoff opened. */
class MediaContinuations {
    private data class Binding(val activity: Activity, val hostId: String)
    private val bindings = linkedMapOf<String, Binding>()

    @Synchronized fun bind(activity: Activity, hostId: String, openedPackage: String?) {
        if (openedPackage == null) return
        bindings.remove(openedPackage)
        if (activity.continuationId == null || activity.url == null) return
        bindings[openedPackage] = Binding(activity.copy(artworkJpegBase64 = null), hostId)
        while (bindings.size > 40) bindings.remove(bindings.keys.first())
    }

    @Synchronized fun enrich(activity: Activity, hostId: String): Activity {
        val binding = bindings[activity.app.id]?.takeIf { it.hostId == hostId } ?: return activity
        val source = binding.activity
        val url = activity.url ?: source.url.takeIf { source.content?.provider == "twitch" || ContentLinks.twitchUrl(it) != null }
        return activity.copy(continuationId = source.continuationId, url = url)
    }

    @Synchronized fun clear() = bindings.clear()
}
