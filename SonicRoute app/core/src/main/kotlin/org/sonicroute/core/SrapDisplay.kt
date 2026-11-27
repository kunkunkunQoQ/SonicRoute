package org.sonicroute.core

import java.io.Closeable
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

data class ScreenUnit(val bytes: ByteArray, val ptsUs: Long, val config: Boolean, val key: Boolean)

/** The codec never waits for the network. A slow reader resumes at an independently decodable frame. */
class ScreenFrames(val width: Int, val height: Int, private val stopCapture: () -> Unit) : Closeable {
    private val lock = Object()
    private val queue = ArrayDeque<ScreenUnit>()
    private var config: ScreenUnit? = null
    private var needKey = true
    private var ended = false

    /** Returns true when backlog loss requires the encoder to produce a new sync frame. */
    fun offer(unit: ScreenUnit): Boolean = synchronized(lock) {
        if (ended || unit.bytes.isEmpty() || unit.bytes.size > MAX_UNIT) return false
        if (unit.config) {
            config = unit
            needKey = true
            queue.clear()
            return false
        }
        val overrun = queue.size >= 4
        if (overrun) { queue.clear(); needKey = true }
        if (needKey) {
            if (!unit.key || config == null) return overrun
            queue.addLast(config!!)
            needKey = false
        }
        queue.addLast(unit)
        lock.notifyAll()
        false
    }

    internal fun read(): ScreenUnit? = synchronized(lock) {
        while (!ended && queue.isEmpty()) lock.wait()
        if (ended) null else queue.removeFirst()
    }

    override fun close() {
        synchronized(lock) {
            if (ended) return
            ended = true
            queue.clear()
            config = null
            lock.notifyAll()
        }
        stopCapture()
    }

    companion object {
        const val MAX_UNIT = 1024 * 1024
        const val CHUNK = 32768
    }
}

interface SrapDisplayBridge {
    fun grantsDisplay(session: String, peer: String): Boolean = false
    fun openDisplay(session: String, peer: String, revision: Long): OperationResult<ScreenFrames> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
}

internal class SrapDisplay(private val bridge: SrapOperationBridge, private val current: () -> Boolean,
    private val allocate: () -> Long, private val send: (Long, ByteArray) -> Boolean) : Closeable {
    private class Active(val id: Long, val session: String, val peer: String, val lease: OperationLease, val frames: ScreenFrames, var opening: String?) {
        val worker = Executors.newSingleThreadExecutor { r -> Thread(r, "SRAP screen sender").apply { isDaemon = true } }
        var seq = 0L
        var request: String? = null
        var lastResponse = System.nanoTime()
        var deadline: java.util.concurrent.ScheduledFuture<*>? = null
    }
    private val lock = Any()
    private var active: Active? = null
    private var closed = false

    fun open(session: String, peer: String, rev: Long, params: JsonObject, request: String): OperationResult<JsonObject> {
        if (params.fields.size != 1 || SrapCodec.requiredText(params, "codec") != "h264") return failure(ProtocolError.INVALID_ARGUMENT)
        if (!current() || !bridge.grantsDisplay(session, peer)) return failure(ProtocolError.PERMISSION_REQUIRED)
        val lease = bridge.currentPermissionSnapshot().acquireLease(Capability.MIRROR_DISPLAY, "one_projection", rev)
        if (lease !is OperationResult.Done) return lease as OperationResult.Failure
        synchronized(lock) { if (closed || active != null) return failure(ProtocolError.BUSY) }
        val id = allocate()
        val opened = bridge.openDisplay(session, peer, rev)
        if (opened !is OperationResult.Done) return opened as? OperationResult.Failure ?: failure(ProtocolError.IO_FAILURE)
        val a = Active(id, session, peer, lease.value, opened.value, request)
        synchronized(lock) {
            if (closed || active != null || !authorized(a)) {
                a.frames.close()
                a.worker.shutdown()
                return failure(ProtocolError.STALE_SESSION)
            }
            active = a
            arm(a)
        }
        return OperationResult.Done(StrictJson.obj("streamId" to StrictJson.num(id), "width" to StrictJson.num(a.frames.width),
            "height" to StrictJson.num(a.frames.height), "codec" to StrictJson.str("h264")))
    }

    fun next(request: String, params: JsonObject, result: (OperationResult<JsonObject>) -> Unit) {
        val id = SrapCodec.requiredLong(params, "streamId")
        val seq = SrapCodec.requiredLong(params, "seq")
        val a = synchronized(lock) {
            val candidate = active?.takeIf { it.id == id } ?: return result(failure(ProtocolError.STALE_SESSION))
            if (params.fields.size != 2 || seq != candidate.seq) return result(failure(ProtocolError.INVALID_ARGUMENT))
            if (candidate.request != null) return result(failure(ProtocolError.BUSY))
            candidate.opening = null
            candidate.request = request
            candidate
        }
        try {
            a.worker.execute {
                val frame = runCatching { a.frames.read() }.getOrNull()
                var offset = 0
                while (frame != null && offset < frame.bytes.size && live(a)) {
                    val end = minOf(offset + ScreenFrames.CHUNK, frame.bytes.size)
                    val bytes = if (offset == 0 && end == frame.bytes.size) frame.bytes else frame.bytes.copyOfRange(offset, end)
                    if (!synchronized(a) { live(a) && send(id, bytes) }) break
                    offset = end
                }
                if (frame == null || offset != frame.bytes.size || !live(a)) {
                    stop(id)
                    result(failure(ProtocolError.PERMISSION_LOST))
                } else {
                    synchronized(lock) {
                        a.request = null
                        a.seq++
                        a.lastResponse = System.nanoTime()
                        if (active === a && a.deadline == null) arm(a)
                    }
                    result(OperationResult.Done(StrictJson.obj("streamId" to StrictJson.num(id), "seq" to StrictJson.num(seq),
                        "size" to StrictJson.num(offset), "ptsUs" to StrictJson.num(frame.ptsUs),
                        "config" to StrictJson.bool(frame.config), "key" to StrictJson.bool(frame.key))))
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) { result(failure(ProtocolError.STALE_SESSION)) }
    }

    private fun authorized(a: Active) = current() && bridge.grantsDisplay(a.session, a.peer) &&
        a.lease.isValidFor(bridge.currentPermissionSnapshot(), requireExactCapabilityRevision = false)
    private fun live(a: Active) = synchronized(lock) { active === a } && authorized(a)
    private fun arm(a: Active) {
        a.deadline = deadlines.schedule({
            val abandoned = synchronized(lock) {
                if (a !== active) return@schedule
                val abandoned = a.request == null && System.nanoTime() - a.lastResponse >= TimeUnit.SECONDS.toNanos(30)
                if (!abandoned && a.request == null) arm(a)
                else if (!abandoned) a.deadline = null
                abandoned
            }
            if (abandoned) stop(a.id)
        }, 30, TimeUnit.SECONDS)
    }
    fun revalidate() { val a = synchronized(lock) { active } ?: return; if (!authorized(a)) stop(a.id) }
    fun cancelRequest(id: String) { synchronized(lock) { active?.takeIf { it.request == id || it.opening == id }?.id }?.let(::stop) }
    fun stop(id: Long): Boolean {
        val a = synchronized(lock) {
            val a = active?.takeIf { it.id == id } ?: return false
            active = null
            a.deadline?.cancel(false)
            a
        }
        synchronized(a) { a.frames.close() }
        a.worker.shutdown()
        return true
    }
    override fun close() {
        synchronized(lock) { closed = true; active?.id }?.let(::stop)
    }
    private fun failure(error: ProtocolError) = OperationResult.Failure(error)
    private companion object {
        val deadlines = ScheduledThreadPoolExecutor(1) { r -> Thread(r,"SRAP screen deadline").apply { isDaemon = true } }
            .apply { removeOnCancelPolicy = true; setKeepAliveTime(5, TimeUnit.SECONDS); allowCoreThreadTimeOut(true) }
    }
}
