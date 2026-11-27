package org.sonicroute.core

import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.util.Timer
import java.util.TimerTask
import java.util.UUID
import java.util.concurrent.Executor

data class SrapClipboardValue(val revision: Long, val text: String?)

object SrapClipboardText {
    fun encode(text: String): ByteArray? {
        if (text.length > SrapCodec.MAX_PAYLOAD || '\u0000' in text || text.contains("sonicroute://pair?", ignoreCase = true)) return null
        val buffer = runCatching { Charsets.UTF_8.newEncoder().encode(CharBuffer.wrap(text)) }.getOrNull() ?: return null
        if (buffer.remaining() > SrapCodec.MAX_PAYLOAD) return null
        return ByteArray(buffer.remaining()).also { buffer.get(it) }
    }

    fun decode(bytes: ByteArray): String? {
        if (bytes.size > SrapCodec.MAX_PAYLOAD) return null
        val text = runCatching { Charsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString() }.getOrNull() ?: return null
        return text.takeUnless { '\u0000' in it || it.contains("sonicroute://pair?", ignoreCase = true) }
    }
}

interface SrapClipboardBridge {
    fun watchClipboard(session: String, capabilities: Long, watchId: String, changed: () -> Unit): OperationResult<Long> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
    fun readClipboard(session: String, capabilities: Long, watchId: String): OperationResult<SrapClipboardValue> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
    fun writeClipboard(session: String, capabilities: Long, watchId: String, expected: Long, text: String): OperationResult<Long> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
    fun unwatchClipboard(session: String, watchId: String) {}
}

internal class SrapClipboardRequests(
    private val bridge: SrapOperationBridge,
    private val executor: Executor,
    private val trusted: (String) -> Boolean,
    private val claim: (Long, Any, (ByteArray) -> Boolean) -> Boolean,
    private val release: (Long, Any) -> Unit,
    private val allocateEven: () -> Long,
    private val respond: (String, String, OperationResult<JsonObject>) -> Unit,
    private val changed: (String, String, Long, Long, ByteArray?) -> Unit
) {
    private class Watch(val session: String, val id: String, val lease: OperationLease) {
        var ready = false
        var dirty = false
        var draining = false
        var sentRevision = -1L
    }
    private class Write(val watch: Watch, val request: String, val stream: Long, val size: Int, val expected: Long, val cap: Long) {
        val deadline = Timer("SonicRoute-clipboard-upload-expiry", true)
    }
    private var watch: Watch? = null
    private var upload: Write? = null
    private var lastUploadStream = 0L

    fun request(session: String, id: String, op: String, params: JsonObject, revision: Long) {
        val result = try {
            val watchId = SrapCodec.requiredText(params, "watchId")
            if (runCatching { UUID.fromString(watchId).toString() }.getOrNull() != watchId) throw SrapProtocolException("invalid_watch")
            val existing = synchronized(this) { watch }
            if (op == "clipboard.unwatch") {
                if (existing != null && existing.id == watchId) stop(existing)
                respond(session, id, OperationResult.Done(StrictJson.obj()))
                return
            }
            val snapshot = bridge.currentPermissionSnapshot()
            val acquired = snapshot.acquireLease(Capability.CLIPBOARD_SYNC, "focused_text", revision)
            if (acquired !is OperationResult.Done) {
                respond(session, id, acquired as OperationResult.Failure)
                return
            }
            if (!trusted(session)) {
                respond(session, id, OperationResult.Failure(ProtocolError.PERMISSION_LOST))
                return
            }
            if (op == "clipboard.watch") {
                if (existing != null) {
                    respond(session, id, OperationResult.Failure(ProtocolError.BUSY))
                    return
                }
                val next = Watch(session, watchId, acquired.value)
                synchronized(this) { watch = next }
                val started = bridge.watchClipboard(session, revision, watchId) { enqueue(next) }
                if (started is OperationResult.Done && current(next) != null) {
                    respond(session, id, doneRevision(started.value))
                    synchronized(this) { next.ready = true; next.sentRevision = started.value }
                    if (synchronized(this) { next.dirty }) enqueue(next)
                } else {
                    stop(next)
                    respond(session, id, started as? OperationResult.Failure ?: OperationResult.Failure(ProtocolError.PERMISSION_LOST))
                }
                return
            }
            if (existing == null || existing.id != watchId || current(existing) == null) {
                OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED)
            } else if (op == "clipboard.get") {
                val value = bridge.readClipboard(session, revision, watchId)
                if (value is OperationResult.Done) {
                    respond(session, id, doneRevision(value.value.revision))
                    emit(existing, value.value, true)
                    return
                }
                value as OperationResult.Failure
            } else {
                val stream = SrapCodec.requiredLong(params, "streamId")
                val size = SrapCodec.requiredInt(params, "byteLength")
                val expected = SrapCodec.requiredLong(params, "expectedRevision")
                if (stream !in 1..0xffffffffL || stream % 2 != 1L || size !in 0..SrapCodec.MAX_PAYLOAD || expected < 0) {
                    throw SrapProtocolException("invalid_clipboard_fields")
                }
                val item = synchronized(this) {
                    if (upload != null) return@synchronized null
                    if (stream <= lastUploadStream) throw SrapProtocolException("stream_id_reused")
                    val item = Write(existing, id, stream, size, expected, revision)
                    if (!claim(stream, item) { bytes -> receive(stream, bytes) }) {
                        item.deadline.cancel()
                        throw SrapProtocolException("stream_id_occupied")
                    }
                    lastUploadStream = stream
                    upload = item
                    item
                }
                if (item == null) OperationResult.Failure(ProtocolError.BUSY) else {
                    if (size == 0) receive(stream, ByteArray(0)) else {
                        synchronized(this) {
                            if (upload !== item) return
                            item.deadline.schedule(object : TimerTask() {
                                override fun run() { cancel(id, stream, ProtocolError.TIMEOUT) }
                            }, 6_000)
                        }
                        respond(session, id, OperationResult.Pending())
                    }
                    return
                }
            }
        } catch (_: SrapProtocolException) { OperationResult.Failure(ProtocolError.INVALID_ARGUMENT) }
        respond(session, id, result)
    }

    fun receive(stream: Long, bytes: ByteArray): Boolean {
        val item = synchronized(this) {
            val entry = upload ?: return false
            if (entry.stream != stream) return false
            upload = null
            entry.deadline.cancel()
            entry
        }
        release(stream, item)
        val result = try {
            val live = current(item.watch)
            val text = if (item.size == bytes.size) SrapClipboardText.decode(bytes) else null
            when {
                live == null -> OperationResult.Failure(ProtocolError.PERMISSION_LOST)
                item.cap != live.capabilityRevision -> OperationResult.Failure(ProtocolError.STALE_SESSION)
                text == null -> OperationResult.Failure(ProtocolError.INVALID_ARGUMENT)
                else -> when (val written = bridge.writeClipboard(item.watch.session, item.cap, item.watch.id, item.expected, text)) {
                    is OperationResult.Done -> doneRevision(written.value)
                    is OperationResult.Failure -> written
                    is OperationResult.Pending -> OperationResult.Failure(ProtocolError.IO_FAILURE)
                }
            }
        } finally { bytes.fill(0) }
        respond(item.watch.session, item.request, result)
        return true
    }

    fun cancel(request: String?, stream: Long?, error: ProtocolError = ProtocolError.CANCELED) {
        val item = synchronized(this) {
            val item = upload ?: return
            if (request != item.request && stream != item.stream) return
            upload = null
            item.deadline.cancel()
            item
        }
        release(item.stream, item)
        respond(item.watch.session, item.request, OperationResult.Failure(error))
    }

    fun update(snapshot: PermissionSnapshot) {
        val w = synchronized(this) { watch } ?: return
        if (!w.lease.isValidFor(snapshot, requireExactCapabilityRevision = false)) stop(w)
    }

    fun close() { synchronized(this) { watch }?.let { stop(it, closing = true) } }

    private fun stop(w: Watch, closing: Boolean = false) {
        val pending = synchronized(this) {
            if (watch !== w) return
            watch = null
            val item = upload
            upload = null
            item?.deadline?.cancel()
            item
        }
        bridge.unwatchClipboard(w.session, w.id)
        // Finish an event that already started before acknowledging unwatch and dropping the receiver.
        synchronized(w) { }
        if (pending != null) {
            release(pending.stream, pending)
            if (!closing && trusted(w.session)) respond(w.session, pending.request, OperationResult.Failure(ProtocolError.PERMISSION_LOST))
        }
    }

    private fun current(w: Watch): PermissionSnapshot? {
        if (!trusted(w.session) || synchronized(this) { watch !== w }) return null
        return bridge.currentPermissionSnapshot().takeIf { w.lease.isValidFor(it, requireExactCapabilityRevision = false) }
    }

    private fun enqueue(w: Watch) {
        synchronized(this) {
            if (watch !== w) return
            w.dirty = true
            if (!w.ready || w.draining) return
            w.draining = true
        }
        executor.execute {
            while (true) {
                synchronized(this) {
                    if (watch !== w || !w.dirty) { w.draining = false; return@execute }
                    w.dirty = false
                }
                val snapshot = current(w) ?: continue
                val value = bridge.readClipboard(w.session, snapshot.capabilityRevision, w.id)
                if (value is OperationResult.Done) emit(w, value.value, false)
            }
        }
    }

    private fun emit(w: Watch, value: SrapClipboardValue, requested: Boolean) {
        val data = value.text?.let(SrapClipboardText::encode)
        try {
            // Serializes snapshots from explicit reads and listener updates on this one session.
            synchronized(w) {
                if (current(w) == null || value.revision < w.sentRevision || !requested && value.revision == w.sentRevision) return
                w.sentRevision = value.revision
                val stream = if (data == null || data.isEmpty()) 0L else allocateEven()
                changed(w.session, w.id, value.revision, stream, data)
            }
        } finally { data?.fill(0) }
    }

    private fun doneRevision(revision: Long) = OperationResult.Done(StrictJson.obj("revision" to StrictJson.num(revision)))
}
