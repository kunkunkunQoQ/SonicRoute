package org.sonicroute.core

import java.io.Closeable
import java.util.concurrent.Executor
import java.util.concurrent.RejectedExecutionException

/** Deliver one peer's notices in order without making the LAN controller wait on its socket. */
internal class SrapPeerNotices(
    private val executor: Executor,
    private val capabilities: (PermissionSnapshot) -> Unit,
    private val sendMediaNotice: () -> Unit,
    private val disconnect: () -> Unit
) : Closeable {
    private sealed interface Notice {
        data class Capabilities(val snapshot: PermissionSnapshot) : Notice
        data object Media : Notice
    }
    private val pending = ArrayDeque<Notice>()
    private var draining = false
    private var closed = false

    fun capabilitiesChanged(snapshot: PermissionSnapshot) = enqueue(Notice.Capabilities(snapshot))
    fun mediaChanged() = enqueue(Notice.Media)

    private fun enqueue(notice: Notice) {
        val overflow = synchronized(pending) {
            if (closed || (notice == Notice.Media && pending.contains(Notice.Media))) return
            if (pending.size >= 16) {
                closed = true
                pending.clear()
                true
            } else {
                pending.addLast(notice)
                if (draining) return
                draining = true
                false
            }
        }
        if (overflow) { disconnect(); return }
        try { executor.execute(::drain) }
        catch (_: RejectedExecutionException) { close(); disconnect() }
    }

    private fun drain() {
        try {
            while (true) {
                val next = synchronized(pending) {
                    pending.removeFirstOrNull() ?: run { draining = false; return }
                }
                when (next) {
                    is Notice.Capabilities -> capabilities(next.snapshot)
                    Notice.Media -> sendMediaNotice()
                }
            }
        } catch (_: Exception) { close(); disconnect() }
    }

    override fun close() = synchronized(pending) { closed = true; pending.clear() }
}
