package dev.baton.android.handoff

import dev.baton.android.protocol.Activity
import dev.baton.android.protocol.PresenceState
import dev.baton.android.protocol.Wire

/**
 * Which of another device's activities is worth offering when the user switches devices, and
 * not offering the same one over and over. Same rule as the PC's HandoffSuggestions.cs.
 */
class Suggestions {
    private val offered = HashMap<String, Long>()

    /** Records [activity] as offered; false when it already was within [REPEAT_AFTER_MS]. */
    @Synchronized
    fun tryOffer(deviceId: String, activity: Activity, nowMs: Long = System.currentTimeMillis()): Boolean {
        offered.entries.removeAll { nowMs - it.value >= REPEAT_AFTER_MS }
        val key = "$deviceId\n${activity.id}\n${activity.title}"
        if (key in offered) return false
        offered[key] = nowMs
        return true
    }

    companion object {
        /** Something that stopped longer ago than this is not what the user was just doing. */
        const val RECENT_MS = 5 * 60_000L

        /** An activity offered once is not offered again for this long. */
        const val REPEAT_AFTER_MS = 30 * 60_000L

        /**
         * The activity to offer from a device, or null: playing media first, else the most recent
         * one touched within [RECENT_MS]. Nothing while that device is in use and not playing,
         * since the user is probably still on it.
         */
        fun pick(activities: List<Activity>, presence: PresenceState, nowMs: Long = System.currentTimeMillis()): Activity? {
            activities.firstOrNull { it.playback?.playing == true }?.let { return it }
            if (presence == PresenceState.Active) return null
            return activities
                .map { it to Wire.parseTime(it.updatedAt) }
                .filter { (_, updated) -> nowMs - updated < RECENT_MS }
                .maxByOrNull { (_, updated) -> updated }
                ?.first
        }
    }
}
