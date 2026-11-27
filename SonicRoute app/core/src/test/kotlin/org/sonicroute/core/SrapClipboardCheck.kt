package org.sonicroute.core

import java.io.File
import java.net.InetAddress
import java.util.UUID
import javax.net.ssl.SSLSocket

private fun clipId() = UUID.randomUUID().toString()
private fun str(text: String) = StrictJson.str(text)
private fun num(n: Long) = StrictJson.num(n)
private class ClipboardEndpoint(val sock: SSLSocket) : AutoCloseable {
    val watch = clipId()
    val sid: String
    var cap: Long
    init {
        send(SrapControlEnvelope("hello", SrapCodec.encodeHello()))
        check(readControl(sock).type == "hello")
        val caps = readControl(sock)
        sid = requireNotNull(caps.session)
        cap = SrapCodec.requiredLong(caps.body, "capabilityRevision")
    }
    fun send(env: SrapControlEnvelope) = SrapCodec.writeFrame(sock.outputStream, SrapCodec.encodeControl(env))
    fun req(op: String, vararg fields: Pair<String, JsonValue>): String {
        val id = clipId()
        val args = linkedMapOf<String, JsonValue>("watchId" to str(watch))
        args.putAll(fields)
        send(SrapControlEnvelope("request", StrictJson.obj("op" to str("clipboard." + op),
            "capabilityRevision" to num(cap), "params" to JsonObject(args)), id, sid))
        return id
    }
    fun reply(id: String, status: String): JsonObject {
        val env = readControl(sock)
        check(env.type == "response" && env.id == id && env.session == sid && SrapCodec.requiredText(env.body, "status") == status)
        return env.body
    }
    fun data(stream: Long, payload: ByteArray) = SrapCodec.writeFrame(sock.outputStream, SrapFrame(SrapFrameType.DATA, stream, payload))
    fun event(hasText: Boolean, version: Long): JsonObject {
        val env = readControl(sock)
        val expected = if (hasText) "clipboard.changed" else "clipboard.revision"
        check(env.type == "event" && env.seq != null && env.session == sid && SrapCodec.requiredText(env.body, "watchId") == watch)
        check(SrapCodec.requiredText(env.body, "name") == expected && SrapCodec.requiredLong(env.body, "revision") == version)
        return env.body
    }
    fun content(text: String, version: Long) {
        val event = event(true, version)
        val stream = SrapCodec.requiredLong(event, "streamId")
        val bytes = SrapClipboardText.encode(text)!!
        check(SrapCodec.requiredInt(event, "byteLength") == bytes.size)
        if (bytes.isEmpty()) { check(stream == 0L); return }
        val frame = requireNotNull(SrapCodec.readFrame(sock.inputStream))
        check(stream > 0 && stream % 2 == 0L && frame.type == SrapFrameType.DATA && frame.streamId == stream && frame.payload.contentEquals(bytes))
    }
    fun set(stream: Long, expected: Long, length: Int) = req("set", "streamId" to num(stream), "expectedRevision" to num(expected), "byteLength" to num(length.toLong()))
    override fun close() = sock.close()
}

private class ClipboardFixtureBridge : SrapOperationBridge by TestOperationBridge() {
    @Volatile var allowed = true
    val watchers = HashMap<String, Pair<String, () -> Unit>>()
    var version = 1L
    var shareable: String? = null
    var actual: String? = null
    override fun currentPermissionSnapshot() = PermissionSnapshot(Backend.STANDARD, 0, emptyMap(),
        if (allowed) 1 else 2, mapOf(Capability.CLIPBOARD_SYNC to CapabilityEntry(
            if (allowed) CapabilityStatus.AVAILABLE else CapabilityStatus.NEEDS_AUTHORIZATION, scope = "focused_text")))
    @Synchronized override fun watchClipboard(session: String, capabilities: Long, watchId: String, changed: () -> Unit): OperationResult<Long> {
        watchers[session] = watchId to changed
        return OperationResult.Done(version)
    }
    @Synchronized override fun unwatchClipboard(session: String, watchId: String) {
        if (watchers[session]?.first == watchId) watchers.remove(session)
    }
    @Synchronized override fun readClipboard(session: String, capabilities: Long, watchId: String): OperationResult<SrapClipboardValue> {
        check(allowed && watchers[session]?.first == watchId)
        return OperationResult.Done(SrapClipboardValue(version, shareable))
    }
    @Synchronized override fun writeClipboard(session: String, capabilities: Long, watchId: String, expected: Long, text: String): OperationResult<Long> {
        check(allowed && watchers[session]?.first == watchId)
        if (version != expected) return OperationResult.Failure(ProtocolError.STALE_SESSION)
        actual = text
        version++
        shareable = null
        return OperationResult.Done(version)
    }
    @Synchronized fun local(text: String?) { shareable = text; version++; signal() }
    @Synchronized fun signal() { watchers.values.forEach { it.second() } }
}

fun main(args: Array<String>) {
    fun hex(s: String) = s.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    File(args.single()).readLines().filter { !it.startsWith('#') && it.isNotBlank() }.forEach { line ->
        val fields = line.split('|')
        val bytes = hex(fields[2])
        check(bytes.size == fields[1].toInt() && SrapClipboardText.encode(SrapClipboardText.decode(bytes)!!)!!.contentEquals(bytes))
        check(SrapCodec.encode(SrapFrame(SrapFrameType.DATA, fields[0].toLong(), bytes)).contentEquals(hex(fields[3])))
    }
    check(SrapClipboardText.encode("x".repeat(65536))!!.size == 65536)
    check(SrapClipboardText.encode("界".repeat(22000)) == null && SrapClipboardText.decode(byteArrayOf(0xc0.toByte())) == null)
    check(SrapClipboardText.encode("\u0000") == null && SrapClipboardText.encode("before SoNiCrOuTe://pAiR? fixture") == null)
    val android = createEcIdentity("clipboard Android")
    val windows = createEcIdentity("clipboard Windows")
    val registry = InMemoryTrustedPeerRegistry().apply { savePeer(TrustedPeer(windows.fingerprintSha256Upper, "test",
        SrapCrypto.base64UrlEncodeNoPad(windows.certificate.publicKey.encoded), 0)) }
    val bridge = ClipboardFixtureBridge()
    val loopback = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))
    SrapTlsServer(android, "fixture", registry, PairingWindowController(), bridge, bindAddress = loopback).use { server ->
        val port = server.start()
        fun connect() = ClipboardEndpoint(openTlsClient(port, windows, android.fingerprintSha256Upper, address = loopback))
        connect().use { a -> connect().use { b ->
            a.reply(a.req("watch"), "done"); b.reply(b.req("watch"), "done")
            bridge.local("A中🌟")
            a.content("A中🌟", 2); b.content("A中🌟", 2)
            val body = SrapClipboardText.encode("PC fixture")!!
            val write = a.set(1, 2, body.size)
            a.reply(write, "pending")
            a.data(1, body)
            val written = a.reply(write, "done")
            check(SrapCodec.requiredLong(SrapCodec.requiredObject(written, "result"), "revision") == 3L)
            check(bridge.actual == "PC fixture")
            bridge.signal()
            a.event(false, 3); b.event(false, 3)
            val stale = a.set(3, 2, 1)
            a.reply(stale, "pending"); a.data(3, byteArrayOf(65))
            check(SrapCodec.requiredText(a.reply(stale, "error"), "error") == "stale_session")
            val invalid = a.set(5, 3, 1)
            a.reply(invalid, "pending"); a.data(5, byteArrayOf(0))
            check(SrapCodec.requiredText(a.reply(invalid, "error"), "error") == "invalid_argument")
            val reused = a.set(5, 3, 1)
            check(SrapCodec.requiredText(a.reply(reused, "error"), "error") == "invalid_argument")
            val foreign = b.req("get", "watchId" to str(a.watch))
            check(SrapCodec.requiredText(b.reply(foreign, "error"), "error") == "permission_required")
            val clear = a.set(7, 3, 0)
            a.reply(clear, "done")
            check(bridge.actual == "")
            b.reply(b.req("unwatch"), "done")
            val pending = a.set(9, 4, 65000)
            a.reply(pending, "pending")
            bridge.allowed = false
            server.broadcastCapabilities(bridge.currentPermissionSnapshot())
            check(SrapCodec.requiredText(a.reply(pending, "error"), "error") == "permission_lost")
            check(readControl(a.sock).type == "capabilities" && readControl(b.sock).type == "capabilities")
            check(bridge.watchers.isEmpty())
        } }
    }
    println("SRAP clipboard checks passed: UTF-8 vectors, limits, two real mTLS sessions, independent revisions, readback, cancellation and no echo.")
}
