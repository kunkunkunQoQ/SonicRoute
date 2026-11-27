package org.sonicroute.core

import java.io.Closeable
import java.nio.ByteBuffer
import java.util.concurrent.Executor
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

interface PlaybackCapture : Closeable {
    /** Stereo PCM16 little endian, complete stereo frames. Closing unblocks a pending read. */
    fun read(buffer: ByteArray, offset: Int, count: Int): Int
}

interface SrapCaptureBridge {
    fun grantsCapture(session: String, peer: String): Boolean = false
    fun openCapture(session: String, revision: Long, peer: String, app: String): OperationResult<PlaybackCapture> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
}

/** Single authenticated connection, with the selected app and peer checked when opening and writing. */
internal class SrapCapture(
    private val bridge: SrapOperationBridge, private val events: Executor, private val streamId: () -> Long,
    private val current: () -> Boolean, private val send: (Long, ByteArray) -> Boolean,
    private val ended: (Long, ProtocolError) -> Unit
) : Closeable {
    private class Active(val id: Long, val session: String, val peer: String, val lease: OperationLease, val input: PlaybackCapture) {
        var sent = 0L
        var acknowledged = 0L
        var position = 0L
        var enabled = false
        var pumping = false
        var activity = System.nanoTime()
        var deadline: java.util.concurrent.ScheduledFuture<*>? = null
    }
    private val lock = Any()
    private var active: Active? = null
    private var closed = false

    fun open(session: String, peer: String, revision: Long, params: JsonObject): OperationResult<JsonObject> {
        val app = SrapCodec.requiredText(params, "appKey")
        if (app.length !in 3..255 || params.fields.size != 3 ||
            SrapCodec.requiredText(params, "encoding") != SrapPcmInput.FORMAT || params["keepPhoneAudio"] != JsonBool(true)) {
            return OperationResult.Failure(ProtocolError.INVALID_ARGUMENT, "requires_nonexclusive_pcm")
        }
        if (!current() || !bridge.grantsCapture(session, peer)) return OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED)
        val grant = bridge.currentPermissionSnapshot().acquireLease(Capability.CAPTURE_APP, "other_app:$app", revision)
        if (grant !is OperationResult.Done) return grant as OperationResult.Failure
        if (synchronized(lock) { closed || active != null }) return OperationResult.Failure(ProtocolError.BUSY)
        val source = bridge.openCapture(session, revision, peer, app)
        if (source !is OperationResult.Done) return source as? OperationResult.Failure ?: OperationResult.Failure(ProtocolError.IO_FAILURE)
        val capture = Active(streamId(), session, peer, grant.value, source.value)
        val accepted = synchronized(lock) {
            if (closed || active != null || !authorized(capture)) false
            else { active = capture; armTimeout(capture, TIMEOUT_MS); true }
        }
        if (!accepted) { source.value.close(); return OperationResult.Failure(ProtocolError.STALE_SESSION) }
        return OperationResult.Done(StrictJson.obj("streamId" to StrictJson.num(capture.id),
            "encoding" to StrictJson.str(SrapPcmInput.FORMAT), "credit" to StrictJson.num(SrapPcmInput.WINDOW)))
    }

    fun credit(params: JsonObject): OperationResult<JsonObject> {
        val id = SrapCodec.requiredLong(params, "streamId")
        val consumed = SrapCodec.requiredLong(params, "consumedPackets")
        if (params.fields.size != 2) return OperationResult.Failure(ProtocolError.INVALID_ARGUMENT)
        val start: Active? = synchronized(lock) {
            val a = active?.takeIf { it.id == id } ?: return OperationResult.Failure(ProtocolError.STALE_SESSION)
            if ((!a.enabled && consumed != 0L) || consumed !in a.acknowledged..a.sent)
                return OperationResult.Failure(ProtocolError.INVALID_ARGUMENT)
            if (!a.enabled || a.acknowledged < consumed) a.activity = System.nanoTime()
            a.acknowledged = consumed
            a.enabled = true
            if (a.pumping || a.sent - consumed >= SrapPcmInput.WINDOW) null else { a.pumping = true; a }
        }
        if (start != null) events.execute { pump(start) }
        return OperationResult.Done(StrictJson.obj("streamId" to StrictJson.num(id), "consumedPackets" to StrictJson.num(consumed)))
    }

    private fun authorized(a: Active) = current() && bridge.grantsCapture(a.session, a.peer) &&
        a.lease.isValidFor(bridge.currentPermissionSnapshot(), requireExactCapabilityRevision = false)

    private fun pump(a: Active) {
        try {
            while (true) {
                synchronized(lock) {
                    if (active !== a) return
                    if (a.sent - a.acknowledged >= SrapPcmInput.WINDOW) { a.pumping = false; return }
                }
                if (!authorized(a)) { stop(a.id, ProtocolError.PERMISSION_LOST); return }
                val buffer = ByteArray(SrapPcmInput.HEADER + 3840)
                val size = a.input.read(buffer, SrapPcmInput.HEADER, 3840)
                if (size !in 4..3840 || size % 4 != 0) { stop(a.id, ProtocolError.IO_FAILURE); return }
                synchronized(lock) {
                    if (active !== a) return
                    ByteBuffer.wrap(buffer).putInt(a.sent.toInt()).putLong(a.position)
                    a.position += size / 4
                    a.sent++
                }
                if (!authorized(a) || !send(a.id, if (size == 3840) buffer else buffer.copyOf(SrapPcmInput.HEADER + size))) {
                    stop(a.id, ProtocolError.PERMISSION_LOST); return
                }
            }
        } catch (_: Exception) { stop(a.id, ProtocolError.IO_FAILURE) }
    }

    private fun armTimeout(a: Active, after: Long) {
        a.deadline = timer.schedule({
            synchronized(lock) {
                if (active !== a) return@schedule
                val remaining = TIMEOUT_MS - TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - a.activity)
                if (remaining > 0) { armTimeout(a, remaining); return@schedule }
            }
            stop(a.id, ProtocolError.TIMEOUT)
        }, after, TimeUnit.MILLISECONDS)
    }

    fun revalidate() {
        val a = synchronized(lock) { active } ?: return
        if (!authorized(a)) stop(a.id, ProtocolError.PERMISSION_LOST)
    }

    fun stop(id: Long, reason: ProtocolError? = null): Boolean {
        val a = synchronized(lock) {
            val a = active?.takeIf { it.id == id } ?: return false
            active = null
            a.deadline?.cancel(false)
            a
        }
        runCatching { a.input.close() }
        if (reason != null) ended(id, reason)
        return true
    }

    override fun close() {
        val id = synchronized(lock) { closed = true; active?.id } ?: return
        stop(id)
    }

    private companion object {
        const val TIMEOUT_MS = 8000L
        val timer = ScheduledThreadPoolExecutor(1) { r -> Thread(r, "SRAP capture deadline").apply { isDaemon = true } }
            .apply { removeOnCancelPolicy = true; setKeepAliveTime(5, TimeUnit.SECONDS); allowCoreThreadTimeOut(true) }
    }
}
