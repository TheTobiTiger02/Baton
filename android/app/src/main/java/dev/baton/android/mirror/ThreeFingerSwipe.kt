package dev.baton.android.mirror

/**
 * Three fingers put down together and moved quickly, read from the touchscreen's raw input events
 * (multitouch protocol B). Runs in the Shizuku helper, which only watches:
 * the touches still reach the app underneath.
 *
 * [feed] returns the move once, when the first of the three fingers lifts: the fingers' average
 * travel as fractions of the screen's natural (portrait) width and height. Whether that is a swipe
 * up or down depends on how the screen is turned, which only the app knows ([Swipes.direction]).
 */
class ThreeFingerSwipe(private val screen: Touchscreen) {
    data class Move(val dx: Float, val dy: Float)

    private class Finger(var x: Int = -1, var y: Int = -1, var down: Boolean = false)

    private val slots = HashMap<Int, Finger>()
    private var slot = 0
    private var startedAt = 0.0
    private var start: Map<Int, Pair<Int, Int>>? = null
    /** More than three fingers, or the three already judged: nothing more until all lift. */
    private var spent = false

    /** One input event, at [seconds]. */
    fun feed(seconds: Double, type: Int, code: Int, value: Int): Move? {
        when (type) {
            EV_ABS -> when (code) {
                ABS_MT_SLOT -> slot = value
                ABS_MT_TRACKING_ID -> finger().down = value != -1
                ABS_MT_POSITION_X -> finger().x = value
                ABS_MT_POSITION_Y -> finger().y = value
            }
            EV_SYN -> if (code == SYN_REPORT) return frame(seconds)
        }
        return null
    }

    /**
     * Reads the touchscreen device [stream] until it ends or fails, calling [onMove] for each move.
     * Each event is a 64-bit input_event: a timeval, then type, code and value.
     */
    fun watch(stream: java.io.InputStream, onMove: (Move) -> Unit) {
        val bytes = ByteArray(EVENT_SIZE)
        val event = java.nio.ByteBuffer.wrap(bytes).order(java.nio.ByteOrder.nativeOrder())
        val input = java.io.DataInputStream(stream)
        while (true) {
            input.readFully(bytes)
            val seconds = event.getLong(0) + event.getLong(8) / 1_000_000.0
            feed(seconds, event.getShort(16).toInt() and 0xffff, event.getShort(18).toInt() and 0xffff, event.getInt(20))?.let(onMove)
        }
    }

    private fun finger() = slots.getOrPut(slot) { Finger() }

    private fun frame(time: Double): Move? {
        val down = slots.filterValues { it.down }
        if (down.isEmpty()) {
            start = null
            spent = false
            return null
        }
        if (spent) return null
        if (down.size > 3) {
            spent = true
            start = null
            return null
        }
        val begun = start
        if (begun == null) {
            if (down.size == 3) {
                start = down.mapValues { it.value.x to it.value.y }
                startedAt = time
            }
            return null
        }
        if (down.keys == begun.keys) return null
        // A finger lifted (or another landed): the gesture is over.
        spent = true
        if (time - startedAt > MAX_SECONDS) return null
        val moves = begun.map { (id, from) -> slots.getValue(id).let { (it.x - from.first) to (it.y - from.second) } }
        return Move(
            moves.sumOf { it.first }.toFloat() / moves.size / screen.width,
            moves.sumOf { it.second }.toFloat() / moves.size / screen.height
        )
    }

    companion object {
        private const val MAX_SECONDS = 1.0
        private const val EVENT_SIZE = 24
        const val EV_SYN = 0
        const val EV_ABS = 3
        const val SYN_REPORT = 0
        const val ABS_MT_SLOT = 0x2f
        const val ABS_MT_POSITION_X = 0x35
        const val ABS_MT_POSITION_Y = 0x36
        const val ABS_MT_TRACKING_ID = 0x39
    }
}

/** The touchscreen's device and its coordinate ranges, from `getevent -pl`. */
data class Touchscreen(val path: String, val width: Int, val height: Int) {
    companion object {
        private val device = Regex("""add device \d+: (\S+)""")
        private val axis = Regex("""(ABS_MT_POSITION_[XY])\s*:\s*value -?\d+, min (-?\d+), max (-?\d+)""")

        /** The first device that is a screen you touch (direct input) with multitouch positions. */
        fun find(description: String): Touchscreen? =
            description.split("add device").drop(1).firstNotNullOfOrNull { block ->
                val text = "add device$block"
                val path = device.find(text)?.groupValues?.get(1) ?: return@firstNotNullOfOrNull null
                if (!text.contains("INPUT_PROP_DIRECT")) return@firstNotNullOfOrNull null
                val ranges = axis.findAll(text).associate { it.groupValues[1] to (it.groupValues[3].toInt() - it.groupValues[2].toInt()) }
                val width = ranges["ABS_MT_POSITION_X"]?.takeIf { it > 0 } ?: return@firstNotNullOfOrNull null
                val height = ranges["ABS_MT_POSITION_Y"]?.takeIf { it > 0 } ?: return@firstNotNullOfOrNull null
                Touchscreen(path, width, height)
            }
    }
}
