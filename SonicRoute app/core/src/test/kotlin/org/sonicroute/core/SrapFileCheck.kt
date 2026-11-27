package org.sonicroute.core

import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

fun main(args: Array<String>) {
    val business = readBusinessVectors(args.single())
    val documents = ConcurrentHashMap<String, ByteArray>()
    var answer: ((Boolean) -> Unit)? = null
    var opened = 0
    var epoch = 7L
    val reading = java.util.concurrent.CountDownLatch(1)
    val released = java.util.concurrent.CountDownLatch(1)
    val backend = object : SharedFileStore {
        override val grant get() = epoch
        override fun list(grant: Long, parent: List<String>) = documents.map { SharedDocument(it.key, false, it.value.size.toLong(), 0) }
        override fun open(grant: Long, parent: List<String>, name: String): SharedFileInput {
            if (name == "slow.bin") return SharedFileInput(1, object : java.io.InputStream() {
                override fun read(): Int { reading.countDown(); check(released.await(3, TimeUnit.SECONDS)); return -1 }
                override fun close() { released.countDown() }
            })
            return documents[name]?.let { SharedFileInput(it.size.toLong(), it.inputStream()) }
                ?: throw SharedFileFailure(ProtocolError.TARGET_UNAVAILABLE, "missing")
        }
        override fun stage(grant: Long, parent: List<String>, name: String): SharedFileOutput {
            opened++
            return object : SharedFileOutput {
                override val stream = ByteArrayOutputStream()
                override fun commit(size: Long, sha256: String, valid: () -> Boolean): String {
                    check(valid() && size == stream.size().toLong())
                    if (documents.putIfAbsent(name, stream.toByteArray()) != null) throw SharedFileFailure(ProtocolError.BUSY, "name_exists")
                    return name
                }
                override fun close() = stream.close()
            }
        }
        override fun confirm(peer: TrustedPeer, parent: List<String>, name: String, size: Long, done: (Boolean) -> Unit): AutoCloseable {
            answer = done
            return AutoCloseable {}
        }
    }
    val snapshot = PermissionSnapshot(Backend.STANDARD, 1, emptyMap(), 3,
        mapOf(Capability.FILE_TRANSFER to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "saf_tree")))
    val incoming = ConcurrentHashMap<Long, (ByteArray) -> Boolean>()
    val received = ByteArrayOutputStream()
    val files = SrapFiles({ backend }, { snapshot }, { true }, { id, _, callback -> incoming.putIfAbsent(id, callback) == null },
        { id, _ -> incoming.remove(id) }, { _, bytes -> received.write(bytes); true })
    val payload = ByteArray(32775) { (it % 193).toByte() }
    val hash = MessageDigest.getInstance("SHA-256").digest(payload).joinToString("") { "%02X".format(it) }
    val peer = TrustedPeer("A".repeat(64), "Fixture", "", 0)
    val root = JsonArray(emptyList())
    fun request(op: String, params: JsonObject): CompletableFuture<OperationResult<JsonObject>> {
        val future = CompletableFuture<OperationResult<JsonObject>>()
        files.execute(peer, UUID.randomUUID().toString(), op, 3, params) { future.complete(it) }
        return future
    }
    fun ok(f: CompletableFuture<OperationResult<JsonObject>>) = (f.get(3, TimeUnit.SECONDS) as OperationResult.Done).value
    fun failed(f: CompletableFuture<OperationResult<JsonObject>>, code: ProtocolError) {
        check((f.get(3, TimeUnit.SECONDS) as OperationResult.Failure).error == code)
    }
    fun offer(id: Int, name: String, size: Int = payload.size, digest: String = hash) = request("file.offer",
        StrictJson.obj("parent" to root, "streamId" to StrictJson.num(id), "name" to StrictJson.str(name),
            "size" to StrictJson.num(size), "sha256" to StrictJson.str(digest)))
    fun chunk(id: Int, offset: Int, size: Int) = request("file.chunk", StrictJson.obj("streamId" to StrictJson.num(id),
        "offset" to StrictJson.num(offset), "count" to StrictJson.num(size)))
    fun finish(id: Int) = request("file.finish", StrictJson.obj("streamId" to StrictJson.num(id)))
    try {
        failed(request("file.list", StrictJson.obj("parent" to JsonArray(listOf(StrictJson.str(".."))))), ProtocolError.INVALID_ARGUMENT)
        val denied = offer(1, "test.bin")
        check(opened == 0 && !incoming.getValue(1)(byteArrayOf(1)))
        answer!!(false)
        failed(denied, ProtocolError.CANCELED)
        check(opened == 0 && documents.isEmpty() && incoming.isEmpty())
        val accepted = offer(3, "test.bin")
        answer!!(true)
        ok(accepted)
        check(incoming.getValue(3)(payload.copyOfRange(0, 32768)))
        check(!incoming.getValue(3)(byteArrayOf(1)))
        ok(chunk(3, 0, 32768))
        check(incoming.getValue(3)(payload.copyOfRange(32768, payload.size)))
        ok(chunk(3, 32768, 7))
        check(SrapCodec.requiredText(ok(finish(3)), "sha256") == hash)
        check(documents.getValue("test.bin").contentEquals(payload) && incoming.isEmpty())
        ok(request("file.open", StrictJson.obj("parent" to root, "streamId" to StrictJson.num(5), "name" to StrictJson.str("test.bin"))))
        ok(chunk(5, 0, 32768))
        ok(chunk(5, 32768, 7))
        check(received.toByteArray().contentEquals(payload) && SrapCodec.requiredText(ok(finish(5)), "sha256") == hash)
        val wrong = offer(7, "bad.bin", 0, "A".repeat(64))
        answer!!(true)
        ok(wrong)
        failed(finish(7), ProtocolError.IO_FAILURE)
        check("bad.bin" !in documents.keys)
        val changed = offer(9, "gone.bin")
        answer!!(true)
        ok(changed)
        epoch++
        files.revalidate()
        check(9L !in incoming.keys)
        failed(finish(9), ProtocolError.TARGET_UNAVAILABLE)
        repeat(27) { documents["page-$it"] = byteArrayOf() }
        val list = ok(request("file.list", StrictJson.obj("parent" to root)))
        check((list["entries"] as JsonArray).items.size == 24)
        val next = ok(request("file.list", StrictJson.obj("parent" to root, "cursor" to list["cursor"])))
        check((next["entries"] as JsonArray).items.size == 4 && !next.has("cursor"))
        val stale = offer(11, "offset.bin")
        answer!!(true)
        ok(stale)
        check(incoming.getValue(11)(byteArrayOf(1)))
        failed(chunk(11, 1, 1), ProtocolError.INVALID_ARGUMENT)
        check(11L !in incoming.keys && "offset.bin" !in documents.keys)
        ok(request("file.open", StrictJson.obj("parent" to root, "streamId" to StrictJson.num(13), "name" to StrictJson.str("slow.bin"))))
        val blocked = chunk(13, 0, 1)
        check(reading.await(2, TimeUnit.SECONDS))
        files.cancelStream(13)
        check(released.await(2, TimeUnit.SECONDS) && blocked.get(2, TimeUnit.SECONDS) is OperationResult.Failure)
        val unsafe = business.getValue("file.unsafe_name")
        check(unsafe.expected == "reject")
        failed(request(SrapCodec.requiredText(unsafe.request().body, "op"), unsafe.params()), ProtocolError.INVALID_ARGUMENT)

        val file = business.getValue("file.binary")
        val meta = file.metadata
        val target = StrictJson.obj("parent" to meta["parent"], "streamId" to meta["streamId"], "name" to meta["name"],
            "size" to meta["size"], "sha256" to meta["sha256"])
        val admitted = request("file.offer", target)
        answer!!(true)
        ok(admitted)
        check(incoming.getValue(file.frame.streamId)(file.frame.payload))
        val part = StrictJson.obj("streamId" to meta["streamId"], "offset" to meta["offset"], "count" to meta["count"])
        check(ok(request("file.chunk", part))["count"] == meta["count"])
        val receipt = ok(request("file.finish", StrictJson.obj("streamId" to meta["streamId"])))
        check(receipt["name"] == meta["name"] && receipt["size"] == meta["size"] && receipt["sha256"] == meta["sha256"])
        check(documents.getValue(SrapCodec.requiredText(meta, "name")).contentEquals(file.frame.payload))
        check(listOf("CON.txt", "../escape", "a/b", "abc.", "\u202Efile", "file:zone").none(SharedFileNames::valid))
        println("SAF protocol fixture passed: consent, two-way DATA, SHA-256, bounds, pagination, grant replacement and cleanup")
    } finally { files.close() }
}
