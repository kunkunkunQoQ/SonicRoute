package org.sonicroute.core

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.Executor

/** A single session-owned PCM16 stereo stream. One TLS reader produces and one AudioTrack consumes. */
class SrapPcmInput internal constructor(
    private val events: Executor,
    private val sendCredit: (Long) -> Unit
) : AutoCloseable {
    private val packets = ArrayBlockingQueue<ByteArray>(WINDOW)
    private var nextSeq = 0L
    private var nextFrame = 0L
    private var consumed = 0L
    private var notifying = false
    @Volatile private var closed = false

    @Synchronized
    internal fun receive(payload: ByteArray): Boolean {
        val bytes = payload.size - HEADER
        if (closed || bytes !in 4..3840 || bytes % 4 != 0 || nextSeq - consumed >= WINDOW) return false
        val header = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        if ((header.int.toLong() and 0xffffffffL) != nextSeq || header.long != nextFrame) return false
        if (!packets.offer(payload)) return false
        ++nextSeq
        nextFrame += bytes / 4
        return true
    }

    /** Returns the original wire payload. The first HEADER bytes are framing, not audio. */
    fun take(): ByteArray? = if (closed) null else packets.take().takeUnless { it.isEmpty() }

    fun written() {
        synchronized(this) {
            if (closed) return
            check(consumed < nextSeq)
            ++consumed
            if (notifying) return
            notifying = true
        }
        events.execute {
            while (true) {
                val value = synchronized(this) { if (closed) return@execute else consumed }
                sendCredit(value)
                synchronized(this) {
                    if (closed || consumed == value) { notifying = false; return@execute }
                }
            }
        }
    }

    @Synchronized
    override fun close() {
        closed = true
        packets.clear()
        packets.offer(end)
    }

    companion object {
        const val FORMAT = "s16le-48000-stereo-v1"
        const val WINDOW = 4
        const val HEADER = 12
        private val end = ByteArray(0)
    }
}
