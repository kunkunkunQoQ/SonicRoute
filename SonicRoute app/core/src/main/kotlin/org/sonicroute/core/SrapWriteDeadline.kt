package org.sonicroute.core

import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

/** Socket read timeouts do not cover blocked TLS writes. One timer covers a burst of frames. */
internal class SrapWriteDeadline(private val abort: () -> Unit) : AutoCloseable {
    private var start = 0L
    private var alarm: ScheduledFuture<*>? = null
    private var closed = false

    // The session serializes calls on its frame-writer lock.
    fun write(action: () -> Unit): Boolean {
        synchronized(this) {
            if (closed) return false
            start = System.nanoTime()
            if (alarm == null) schedule(TIMEOUT)
        }
        return try { action(); true } catch (_: java.io.IOException) {
            abort()
            false
        } finally { synchronized(this) { start = 0L } }
    }

    private fun schedule(delay: Long) {
        alarm = scheduler.schedule(checkWrite@{
            synchronized(this) {
                alarm = null
                if (closed || start == 0L) return@checkWrite
                val remaining = TIMEOUT - (System.nanoTime() - start)
                if (remaining > 0) { schedule(remaining); return@checkWrite }
                closed = true
            }
            abort()
        }, delay, TimeUnit.NANOSECONDS)
    }

    @Synchronized override fun close() {
        closed = true
        alarm?.cancel(false)
        alarm = null
    }
    private companion object {
        val TIMEOUT = TimeUnit.SECONDS.toNanos(6)
        val scheduler = ScheduledThreadPoolExecutor(1) { job -> Thread(job, "SRAP write deadline").apply { isDaemon = true } }
            .apply { removeOnCancelPolicy = true; setKeepAliveTime(8, TimeUnit.SECONDS); allowCoreThreadTimeOut(true) }
    }
}
