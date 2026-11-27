package org.sonicroute.core

import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

data class SharedDocument(val name: String, val directory: Boolean, val size: Long?, val modified: Long)
data class SharedFileInput(val size: Long, val stream: InputStream)
interface SharedFileOutput : Closeable {
    val stream: OutputStream
    fun commit(size: Long, sha256: String, valid: () -> Boolean): String
}
class SharedFileFailure(val code: ProtocolError, val reason: String) : Exception(reason)
interface SharedFileStore {
    /** Negative while no tree is shared. Changes whenever the user replaces or withdraws the tree. */
    val grant: Long
    fun isGranted(epoch: Long): Boolean = grant == epoch
    fun permissionLost() {}
    fun list(grant: Long, parent: List<String>): List<SharedDocument>
    fun open(grant: Long, parent: List<String>, name: String): SharedFileInput
    fun stage(grant: Long, parent: List<String>, name: String): SharedFileOutput
    fun confirm(peer: TrustedPeer, parent: List<String>, name: String, size: Long, done: (Boolean) -> Unit): AutoCloseable
}

object SharedFileNames {
    fun valid(name: String): Boolean {
        if (name.isBlank() || name.length > 255 || name == "." || name == ".." || name.endsWith('.') || name.endsWith(' ')) return false
        if (name.any { it.isISOControl() || Character.getType(it).toByte() == Character.FORMAT ||
                it in "/\\:<>\"|?*" || it == '\u2028' || it == '\u2029' }) return false
        val stem = name.substringBefore('.').trimEnd().uppercase(java.util.Locale.ROOT)
        return stem !in setOf("CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$") &&
            !(stem.length == 4 && (stem.startsWith("COM") || stem.startsWith("LPT")) && stem.last() in "0123456789¹²³")
    }
}

internal class SrapFiles(
    private val store: () -> SharedFileStore?,
    private val permission: () -> PermissionSnapshot,
    private val authenticated: () -> Boolean,
    private val claim: (Long, Any, (ByteArray) -> Boolean) -> Boolean,
    private val release: (Long, Any) -> Unit,
    private val sendData: (Long, ByteArray) -> Boolean
) : AutoCloseable {
    private class Transfer(val id: Long, val grant: Long, val lease: OperationLease, val parent: List<String>,
                           val name: String, val upload: Boolean, var size: Long, val expectedHash: String?) {
        val digest = MessageDigest.getInstance("SHA-256")
        @Volatile var canceled = false
        var offset = 0L
        var packet: ByteArray? = null
        var confirmed = false
        var consent: AutoCloseable? = null
        var timeout: ScheduledFuture<*>? = null
        var input: InputStream? = null
        var output: SharedFileOutput? = null
    }
    private data class Page(val key: String, val parent: List<String>, val grant: Long, val entries: List<SharedDocument>, val next: Int)
    private class Request(val id: String) { @Volatile var transfer: Transfer? = null }
    private val lock = Any()
    private val io = ThreadPoolExecutor(0, 1, 5, TimeUnit.SECONDS, ArrayBlockingQueue(4),
        { r -> Thread(r, "SonicRoute-file").apply { isDaemon = true } })
    private var active: Transfer? = null
    private var page: Page? = null
    private var request: Request? = null
    private var closed = false

    fun execute(peer: TrustedPeer, requestId: String, op: String, revision: Long, params: JsonObject,
                done: (OperationResult<JsonObject>) -> Unit) {
        val backend = store()
        val lease = permission().acquireLease(Capability.FILE_TRANSFER, "saf_tree", revision)
        if (lease !is OperationResult.Done) { done(lease as OperationResult.Failure); return }
        if (backend == null || backend.grant < 0 || !authenticated()) {
            done(OperationResult.Failure(ProtocolError.PERMISSION_LOST)); return
        }
        val ticket = Request(requestId)
        synchronized(lock) {
            if (closed || request != null) { done(OperationResult.Failure(ProtocolError.BUSY)); return }
            request = ticket
        }
        fun reply(result: OperationResult<JsonObject>) {
            val own = synchronized(lock) { if (request === ticket) { request = null; true } else false }
            if (own) done(result)
        }
        val grant = backend.grant
        fun work(task: () -> JsonObject) {
            try { io.execute {
                val result = try {
                    ensure(synchronized(lock) { request === ticket && !closed } && authenticated() &&
                        backend.isGranted(grant) && lease.value.isValidFor(permission(), false), ProtocolError.PERMISSION_LOST)
                    val value = task()
                    ensure(backend.grant == grant && authenticated() && lease.value.isValidFor(permission(), false), ProtocolError.PERMISSION_LOST)
                    OperationResult.Done(value)
                } catch (e: SharedFileFailure) { OperationResult.Failure(e.code, e.reason) }
                catch (_: SrapProtocolException) { OperationResult.Failure(ProtocolError.INVALID_ARGUMENT) }
                catch (_: SecurityException) { backend.permissionLost(); OperationResult.Failure(ProtocolError.PERMISSION_LOST) }
                catch (_: Exception) { OperationResult.Failure(ProtocolError.IO_FAILURE) }
                if (result is OperationResult.Failure) ticket.transfer?.let(::cancelTransfer)
                reply(result)
            } } catch (_: java.util.concurrent.RejectedExecutionException) { reply(OperationResult.Failure(ProtocolError.BUSY)) }
        }
        if (op == "file.list") {
            work {
                val parent = parents(params)
                val cursor = SrapCodec.optionalText(params, "cursor")
                val (list, from) = if (cursor == null) {
                    val items = backend.list(grant, parent)
                    ensure(items.size <= 4096, ProtocolError.UNSUPPORTED, "folder_too_large")
                    items.sortedWith(compareByDescending<SharedDocument> { it.directory }.thenBy { it.name }) to 0
                } else synchronized(lock) {
                    val entry = page
                    ensure(entry != null && entry.key == cursor && entry.grant == grant && entry.parent == parent, ProtocolError.STALE_SESSION)
                    entry!!.entries to entry.next
                }
                val batch = list.drop(from).take(24)
                val next = if (from + batch.size < list.size) Page(UUID.randomUUID().toString(), parent, grant, list, from + batch.size) else null
                synchronized(lock) { if (!closed && request === ticket) page = next }
                StrictJson.obj("entries" to JsonArray(batch.map {
                    StrictJson.obj("name" to StrictJson.str(it.name), "directory" to StrictJson.bool(it.directory),
                        "size" to it.size?.let(StrictJson::num), "modified" to StrictJson.num(it.modified))
                }), "cursor" to next?.key?.let(StrictJson::str))
            }
            return
        }
        try {
            val id = SrapCodec.requiredLong(params, "streamId")
            ensure(id in 1..0xffffffffL && id % 2 == 1L)
            if (op == "file.offer" || op == "file.open") {
                val parent = parents(params)
                val name = SrapCodec.requiredText(params, "name")
                ensure(SharedFileNames.valid(name))
                val upload = op == "file.offer"
                val size = if (upload) SrapCodec.requiredLong(params, "size") else 0
                ensure(size in 0..MAX_FILE, ProtocolError.UNSUPPORTED, "file_too_large")
                val hash = if (upload) SrapCodec.requiredText(params, "sha256").uppercase(java.util.Locale.ROOT) else null
                ensure(hash == null || hash.matches(Regex("[0-9A-F]{64}")))
                val t = Transfer(id, grant, lease.value, parent, name, upload, size, hash)
                synchronized(lock) {
                    ensure(active == null && !closed, ProtocolError.BUSY)
                    ensure(claim(id, t) { bytes -> receive(t, bytes) }, ProtocolError.BUSY)
                    active = t
                    ticket.transfer = t
                }
                fun start() = work {
                    ensure(valid(t), ProtocolError.CANCELED)
                    if (upload) {
                        val output = backend.stage(grant, parent, name)
                        val retained = synchronized(lock) { if (t.canceled) false else { t.output = output; true } }
                        if (!retained) { output.close(); throw SharedFileFailure(ProtocolError.CANCELED, "file_canceled") }
                    } else {
                        val input = backend.open(grant, parent, name)
                        val retained = synchronized(lock) {
                            if (t.canceled) false else { t.input = input.stream; t.size = input.size; true }
                        }
                        if (!retained) { input.stream.close(); throw SharedFileFailure(ProtocolError.CANCELED, "file_canceled") }
                    }
                    ensure(t.size in 0..MAX_FILE, ProtocolError.UNSUPPORTED, "file_size_unavailable")
                    ensure(valid(t), ProtocolError.PERMISSION_LOST)
                    synchronized(lock) { t.confirmed = true }
                    renew(t)
                    StrictJson.obj("streamId" to StrictJson.num(id), "size" to StrictJson.num(t.size))
                }
                if (upload) {
                    t.timeout = deadlines.schedule({
                        cancelTransfer(t)
                        reply(OperationResult.Failure(ProtocolError.TIMEOUT))
                    }, 60, TimeUnit.SECONDS)
                    val admission = backend.confirm(peer, parent, name, size) { yes ->
                        t.timeout?.cancel(false)
                        if (yes && valid(t)) start() else {
                            cancelTransfer(t)
                            reply(OperationResult.Failure(ProtocolError.CANCELED))
                        }
                    }
                    synchronized(lock) { if (t.canceled) admission.close() else t.consent = admission }
                } else start()
                return
            }
            val t = synchronized(lock) { active }
            ensure(t != null && t.id == id && t.confirmed, ProtocolError.TARGET_UNAVAILABLE)
            requireNotNull(t)
            ticket.transfer = t
            ensure(valid(t), ProtocolError.PERMISSION_LOST)
            when (op) {
                "file.chunk" -> work {
                    val offset = SrapCodec.requiredLong(params, "offset")
                    val count = SrapCodec.requiredLong(params, "count")
                    ensure(valid(t) && t.offset == offset && count in 1..32768 && count <= t.size - offset)
                    val bytes = if (t.upload) {
                        val packet = synchronized(lock) { t.packet.also { t.packet = null } }
                        ensure(packet != null && packet.size.toLong() == count)
                        t.output!!.stream.write(packet!!)
                        packet
                    } else {
                        ByteArray(count.toInt()).also { buf ->
                            java.io.DataInputStream(t.input!!).readFully(buf)
                            ensure(valid(t) && sendData(id, buf), ProtocolError.CANCELED)
                        }
                    }
                    t.digest.update(bytes)
                    t.offset += count
                    renew(t)
                    StrictJson.obj("offset" to StrictJson.num(offset), "count" to StrictJson.num(count))
                }
                "file.finish" -> work {
                    ensure(valid(t) && t.offset == t.size && synchronized(lock) { t.packet == null })
                    renew(t, 600)
                    val hash = t.digest.digest().joinToString("") { "%02X".format(java.util.Locale.ROOT, it) }
                    val name = if (t.upload) {
                        ensure(hash == t.expectedHash, ProtocolError.IO_FAILURE, "checksum_mismatch")
                        t.output!!.commit(t.size, hash) { valid(t) }
                    } else {
                        ensure(t.input!!.read() == -1, ProtocolError.IO_FAILURE, "file_changed")
                        t.name
                    }
                    cancelTransfer(t)
                    StrictJson.obj("size" to StrictJson.num(t.size), "sha256" to StrictJson.str(hash), "name" to StrictJson.str(name))
                }
                else -> throw SharedFileFailure(ProtocolError.INVALID_ARGUMENT, "unknown_file_operation")
            }
        } catch (e: SharedFileFailure) { reply(OperationResult.Failure(e.code, e.reason)) }
        catch (_: Exception) { reply(OperationResult.Failure(ProtocolError.INVALID_ARGUMENT)) }
    }

    private fun valid(t: Transfer) = !t.canceled && authenticated() && store()?.grant == t.grant && t.lease.isValidFor(permission(), false)
    private fun receive(t: Transfer, bytes: ByteArray): Boolean {
        if (!valid(t)) return false
        return synchronized(lock) {
            if (active !== t || !t.confirmed || !t.upload || t.packet != null || bytes.size !in 1..32768) false
            else { t.packet = bytes; true }
        }
    }
    private fun renew(t: Transfer, seconds: Long = 60) {
        synchronized(lock) {
            t.timeout?.cancel(false)
            if (!t.canceled) t.timeout = deadlines.schedule({ cancelTransfer(t) }, seconds, TimeUnit.SECONDS)
        }
    }
    fun cancelRequest(id: String) {
        val stream = synchronized(lock) {
            val current = request ?: return
            if (current.id != id) return
            request = null
            current.transfer
        }
        stream?.let(::cancelTransfer)
    }
    fun cancelStream(id: Long?) {
        synchronized(lock) { active }?.takeIf { it.id == id }?.let(::cancelTransfer)
    }
    private fun cancelTransfer(t: Transfer) {
        synchronized(lock) {
            if (active !== t) return
            t.canceled = true
            active = null
        }
        release(t.id, t)
        t.timeout?.cancel(false)
        t.consent?.close()
        if (t.input != null || t.output != null) closers.execute { runCatching { t.input?.close() }; runCatching { t.output?.close() } }
    }
    fun revalidate() {
        val current = synchronized(lock) { active } ?: return
        if (!valid(current)) cancelTransfer(current)
    }
    override fun close() {
        val id = synchronized(lock) { closed = true; request = null; page = null; active?.id }
        cancelStream(id)
        io.shutdown()
    }
    private fun parents(params: JsonObject): List<String> {
        val arr = params["parent"] as? JsonArray ?: throw SharedFileFailure(ProtocolError.INVALID_ARGUMENT, "parent")
        ensure(arr.items.size <= 16)
        return arr.items.map { value ->
            val name = (value as? JsonString)?.value ?: throw SharedFileFailure(ProtocolError.INVALID_ARGUMENT, "name")
            ensure(SharedFileNames.valid(name))
            name
        }
    }
    companion object {
        const val MAX_FILE = 512L * 1024 * 1024
        private val closers = ThreadPoolExecutor(2, 2, 5, TimeUnit.SECONDS, ArrayBlockingQueue(32),
            { r -> Thread(r, "SonicRoute-file-close").apply { isDaemon = true } }, ThreadPoolExecutor.CallerRunsPolicy())
            .apply { allowCoreThreadTimeOut(true) }
        val deadlines = ScheduledThreadPoolExecutor(1) { r -> Thread(r, "SonicRoute-file-deadlines").apply { isDaemon = true } }.apply {
            removeOnCancelPolicy = true
            setKeepAliveTime(5, TimeUnit.SECONDS)
            allowCoreThreadTimeOut(true)
        }
        fun ensure(ok: Boolean, error: ProtocolError = ProtocolError.INVALID_ARGUMENT, reason: String = error.wireId) {
            if (!ok) throw SharedFileFailure(error, reason)
        }
    }
}
