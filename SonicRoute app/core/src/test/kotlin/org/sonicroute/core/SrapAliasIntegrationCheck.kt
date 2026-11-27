package org.sonicroute.core

import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.file.Files
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SSLSocket

private val loopback = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))
private fun id() = UUID.randomUUID().toString()
private fun text(body: JsonObject, name: String) = SrapCodec.requiredText(body, name)
private fun binary(body: JsonObject, name: String) = SrapCrypto.base64UrlDecodeNoPad(text(body, name))
private fun send(socket: SSLSocket, env: SrapControlEnvelope) = SrapCodec.writeFrame(socket.outputStream, SrapCodec.encodeControl(env))
private fun SrapServerIdentity.asPeer(name: String) = TrustedPeer(fingerprintSha256Upper, name,
    SrapCrypto.base64UrlEncodeNoPad(certificate.publicKey.encoded), System.currentTimeMillis())

fun main(args: Array<String>) {
    checkVector(File(args.single()))
    checkInvitations()
    val android = createEcIdentity("Alias Android fixture")
    val windows = createEcIdentity("Alias Windows fixture")
    val other = createEcIdentity("Alias second Windows fixture")
    checkTrustStore(windows.asPeer("Computer A"), other.asPeer("Computer B"))
    checkDeadlines(android, windows.asPeer("Computer A"))
    AliasFixture(android, windows, other).run()
    println("SRAP alias checks passed: fixed transcripts, strict invitations, durable revocation, consent and proof deadlines, loopback mTLS, cancellation, teardown and admission.")
}

private fun checkVector(path: File) {
    val fields = path.readLines().filter { '=' in it && !it.startsWith('#') }.associate { it.substringBefore('=') to it.substringAfter('=') }
    fun bytes(key: String) = fields.getValue(key).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    val transcript = SrapAliasTranscript(bytes("bindId"), bytes("challenge"), bytes("androidPin"), bytes("windowsPin"),
        bytes("profileRef"), bytes("attachment"), fields.getValue("parentSession"), fields.getValue("port").toInt(),
        fields.getValue("major").toInt(), fields.getValue("minor").toInt())
    check(transcript.encode(1).size == 195)
    check(transcript.encode(1).contentEquals(bytes("windowsTranscript")))
    check(transcript.encode(2).contentEquals(bytes("androidTranscript")))
}

private fun checkInvitations() {
    val fp = "53".repeat(32)
    val invite = SrapPairingInvite.create(fp, "192.0.2.23", 1234)
    val uri = invite.toCopyableText()
    val base = uri.substringBefore("&h=")
    check(SrapPairingInvite.parse(uri).hostHint == "192.0.2.23")
    check(SrapPairingInvite.parse(base).hostHint == null)
    check(SrapPairingInvite.parse(base + "&h=[fe80::10%2512]&p=1").portHint == 1)
    check(SrapPairingInvite.parse(base + "&h=2001:db8::2&p=65535").hostHint == "2001:db8::2")
    for (suffix in listOf("&z=1", "&v=1", "#x", "&h=::1&p=2", "&h=224.1.2.3&p=2", "&h=127.1.2.3&p=2",
        "&h=0.0.0.0&p=2", "&h=192.000.2.23&p=2", "&h=::ffff:192.0.2.1&p=2", "&h=ff02::1&p=2",
        "&h=fe80::1&p=2", "&h=[fe80::1%25wlan0]&p=2", "&h=example.local&p=2", "&h=192.0.2.23",
        "&p=2", "&h=192.0.2.23&p=02", "&h=192.0.2.23&p=65536")) {
        check(runCatching { SrapPairingInvite.parse(base + suffix) }.isFailure)
    }
    check(runCatching { SrapPairingInvite.parse(uri.replace("?v=1", "?v=01")) }.isFailure)
    check(runCatching { SrapPairingInvite.parse(uri.replace(invite.secretB64, "A".repeat(44))) }.isFailure)
    for (unsafe in listOf("127.0.0.1", "wlan0", "[fe80::1%2512]")) {
        check(!SrapPairingInvite.create(fp, unsafe, 2000).toCopyableText().contains("&h="))
    }
    check(!invite.toString().contains(invite.secretB64))
}

private fun checkTrustStore(first: TrustedPeer, second: TrustedPeer) {
    val dir = Files.createTempDirectory("srap-trust-test-").toFile()
    val primary = File(dir, "srap_trusted_peers.json")
    val old = File(dir, primary.name + ".bak")
    val held = File(dir, "committed.json")
    val blocker = File(primary, "block")
    try {
        val store = SrapPeerFileStore(dir)
        store.savePeer(first)
        store.savePeer(second)
        primary.copyTo(old)
        check(store.removePeer(first.fingerprintSha256))
        check(SrapPeerFileStore(dir).listPeers() == listOf(second))
        check(!store.removePeer(first.fingerprintSha256))
        check(primary.renameTo(held))
        check(primary.mkdir())
        blocker.writeText("fixture")
        check(runCatching { store.removePeer(second.fingerprintSha256) }.isFailure)
        check(runCatching { store.savePeer(first) }.isFailure)
        check(store.listPeers() == listOf(second))
        check(blocker.delete() && primary.delete())
        check(SrapPeerFileStore(dir).listPeers().isEmpty())
        primary.writeText("damaged fixture")
        check(SrapPeerFileStore(dir).listPeers().isEmpty())
        val wrongKey = first.copy(publicKeyDerB64 = second.publicKeyDerB64)
        check(runCatching { store.savePeer(wrongKey) }.isFailure)
    } finally {
        listOf(blocker, primary, old, held, dir).forEach { it.delete() }
    }
}

private val profile = SrapCrypto.randomBytes(32)
private val attachment = SrapCrypto.randomBytes(16)
private val params = StrictJson.obj(
    "profileRef" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(profile)),
    "attachment" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(attachment))
)

private fun checkDeadlines(android: SrapServerIdentity, peer: TrustedPeer) {
    val responses = LinkedBlockingQueue<OperationResult<JsonObject>>()
    val changes = LinkedBlockingQueue<Unit>()
    val binding = SrapAliasBinding(android, "fixture", { changes.offer(Unit) }, consentMs = 180, proofMs = 450)
    val parent = SrapAliasBinding.Parent(SrapCrypto.randomSessionId(), id(), 0, peer,
        checkAccess = { null }, respond = { responses.offer(it) }, sendProof = { error("proof without a valid signature") })
    check(binding.begin(parent, params) == null)
    check((responses.poll(4, TimeUnit.SECONDS) as OperationResult.Failure).error == ProtocolError.TIMEOUT)
    check(binding.prompt() == null)
    check(binding.begin(parent, params) == null)
    binding.decide(binding.prompt()!!.confirmationId, true)
    val result = (responses.poll(4, TimeUnit.SECONDS) as OperationResult.Done).value
    val port = SrapCodec.requiredInt(result, "port")
    Socket(loopback, port).use { raw ->
        raw.soTimeout = 3000
        checkTcpClosed(raw)
    }
    check(binding.prompt() == null)
    checkPortClosed(port)
}

private fun checkTcpClosed(socket: Socket) {
    // A TCP reset is valid on either VM. A read timeout means the server kept the transport alive.
    try {
        val input = java.io.DataInputStream(socket.inputStream)
        var alerts = 0
        while (true) {
            val first = input.read()
            if (first < 0) return
            // A raw peer that never sent ClientHello can receive a TLS alert before FIN.
            check(socket !is SSLSocket && first == 21 && ++alerts <= 2)
            check(input.readUnsignedByte() == 3 && input.readUnsignedByte() in 0..4)
            check(input.readUnsignedShort() == 2)
            input.readUnsignedShort()
        }
    } catch (e: SocketTimeoutException) { throw e }
    catch (_: IOException) { }
}

private fun checkPortClosed(port: Int) {
    Socket().use {
        check(runCatching { it.connect(InetSocketAddress(loopback, port), 600) }.isFailure) { "proof port remained open" }
    }
}

private class AliasFixture(val android: SrapServerIdentity, val windows: SrapServerIdentity, val other: SrapServerIdentity) {
    val bridge = TestOperationBridge()
    val registry = InMemoryTrustedPeerRegistry()
    val updates = LinkedBlockingQueue<Unit>()
    val clock = AtomicLong(1000)
    val server = SrapTlsServer(android, "fixture", registry, PairingWindowController(), bridge,
        bindAddress = loopback, onAliasChanged = { updates.offer(Unit) }, elapsedMs = clock::get)

    inner class Main(val socket: SSLSocket) : AutoCloseable {
        val session: String
        var revision: Long
        init {
            send(socket, SrapControlEnvelope("hello", SrapCodec.encodeHello()))
            check(readControl(socket).type == "hello")
            val caps = readControl(socket)
            session = requireNotNull(caps.session)
            val state = SrapCodec.parseCapabilitiesBody(caps.body)
            check(state.entries.getValue(Capability.ADB_ALIAS).status == CapabilityStatus.AVAILABLE)
            revision = state.capabilityRevision
        }
        fun begin(capRev: Long = revision): String {
            val request = id()
            send(socket, SrapControlEnvelope("request", StrictJson.obj("op" to StrictJson.str("alias.bind.begin"),
                "capabilityRevision" to StrictJson.num(capRev), "params" to params), id = request, session = session))
            return request
        }
        fun response(request: String): JsonObject {
            val reply = readControl(socket)
            check(reply.type == "response" && reply.id == request && reply.session == session)
            return reply.body
        }
        fun issue(): Grant {
            val request = begin()
            val prompt = waitPrompt()
            check(prompt.peerFingerprintSha256 == windows.fingerprintSha256Upper && !prompt.awaitingProof)
            server.decideAliasBinding(prompt.confirmationId, true)
            val response = response(request)
            check(text(response, "status") == "done")
            val result = SrapCodec.requiredObject(response, "result")
            check(SrapCodec.requiredInt(result, "expiresInMs") == 30000)
            return Grant(request, session, result)
        }
        fun cancel(request: String): String {
            val cancel = id()
            send(socket, SrapControlEnvelope("cancel", StrictJson.obj("requestId" to StrictJson.str(request)),
                id = cancel, session = session))
            return cancel
        }
        override fun close() = socket.close()
    }

    inner class Grant(val request: String, val session: String, val result: JsonObject) {
        val port = SrapCodec.requiredInt(result, "port")
        val transcript = SrapAliasTranscript(binary(result, "bindId"), binary(result, "challenge"),
            android.spkiSha256Bytes, windows.spkiSha256Bytes, profile, attachment, session, port, 1, 0)
        fun prove(socket: SSLSocket, role: Int = 1, parent: String = session) {
            val signature = SrapCrypto.signEcdsaP256Sha256(windows.privateKey, transcript.encode(role))
            send(socket, SrapControlEnvelope("alias.bind.prove", StrictJson.obj(
                "bindId" to result["bindId"], "profileRef" to params["profileRef"], "attachment" to params["attachment"],
                "parentSession" to StrictJson.str(parent),
                "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(signature))
            ), id = id()))
        }
    }

    fun waitPrompt(): SrapAliasPrompt {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (true) {
            server.aliasPrompt()?.let { return it }
            check(updates.poll(deadline - System.nanoTime(), TimeUnit.NANOSECONDS) != null) { "confirmation did not appear" }
        }
    }
    fun waitClosed() {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (server.aliasPrompt() != null) {
            check(updates.poll(deadline - System.nanoTime(), TimeUnit.NANOSECONDS) != null) { "confirmation remained active" }
        }
    }
    fun tls(port: Int, who: SrapServerIdentity? = windows, protocol: String? = null) =
        openTlsClient(port, who, android.fingerprintSha256Upper, protocol, loopback)
    fun helloProof(socket: SSLSocket) {
        send(socket, SrapControlEnvelope("hello", SrapCodec.encodeHello()))
        val hello = readControl(socket)
        check(hello.type == "hello" && hello.session == null)
    }

    fun run() {
        registry.savePeer(windows.asPeer("Fixture Windows"))
        registry.savePeer(other.asPeer("Other trusted computer"))
        server.use {
            val port = server.start()
            Main(tls(port)).use { main ->
                val pending = main.begin()
                val first = waitPrompt()
                server.decideAliasBinding("wrong-confirmation", true)
                check(!server.aliasPrompt()!!.awaitingProof)
                check(text(main.response(main.begin()), "error") == "busy")
                server.decideAliasBinding(first.confirmationId, false)
                check(text(main.response(pending), "error") == "canceled")
                val stale = main.begin(main.revision + 1)
                check(text(main.response(stale), "error") == "stale_session")
                val awaiting = main.begin()
                waitPrompt()
                val cancelId = main.cancel(awaiting)
                val canceled = listOf(readControl(main.socket), readControl(main.socket)).associateBy { it.id }
                check(text(canceled.getValue(awaiting).body, "error") == "canceled")
                check(text(canceled.getValue(cancelId).body, "status") == "done")

                for (who in listOf(other, null)) {
                    val grant = main.issue()
                    check(runCatching { tls(grant.port, who).use { helloProof(it) } }.isFailure)
                    waitClosed()
                    checkPortClosed(grant.port)
                }
                for (mismatch in listOf("role", "session", "business", "expiry")) {
                    val grant = main.issue()
                    tls(grant.port).use { proof ->
                        helloProof(proof)
                        when (mismatch) {
                            "role" -> grant.prove(proof, role = 2)
                            "session" -> grant.prove(proof, parent = SrapCrypto.randomSessionId())
                            "business" -> send(proof, SrapControlEnvelope("request", StrictJson.obj(), id = id(), session = main.session))
                            "expiry" -> { clock.addAndGet(30_001); grant.prove(proof) }
                        }
                        val error = readControl(proof)
                        val expected = when (mismatch) { "business" -> "invalid_argument"; "expiry" -> "timeout"; else -> "identity_conflict" }
                        check(error.type == "alias.bind.result" && text(error.body, "error") == expected)
                    }
                    waitClosed()
                }
                var priorSeq = -1L
                for (protocol in listOf("TLSv1.3", "TLSv1.2")) {
                    val grant = main.issue()
                    tls(grant.port, protocol = protocol).use { proof ->
                        helloProof(proof)
                        grant.prove(proof)
                        check(SrapCodec.readFrame(proof.inputStream) == null)
                    }
                    val event = readControl(main.socket)
                    check(event.type == "event" && event.session == main.session && event.seq!! > priorSeq)
                    priorSeq = event.seq!!
                    check(text(event.body, "name") == "alias.bind.proof" && text(event.body, "requestId") == grant.request)
                    check(text(event.body, "bindId") == text(grant.result, "bindId"))
                    check(binary(event.body, "attachment").contentEquals(attachment) && binary(event.body, "profileRef").contentEquals(profile))
                    val receipt = binary(event.body, "signature")
                    check(SrapCrypto.verifyEcdsaP256Sha256(android.certificate.publicKey.encoded, grant.transcript.encode(2), receipt))
                    check(!SrapCrypto.verifyEcdsaP256Sha256(android.certificate.publicKey.encoded, grant.transcript.encode(1), receipt))
                    checkPortClosed(grant.port)
                }
                val held = main.issue()
                Socket(loopback, held.port).use { raw ->
                    raw.soTimeout = 3000
                    val cancel = main.cancel(held.request)
                    check(text(main.response(cancel), "status") == "done")
                    checkTcpClosed(raw)
                }
                val changing = main.issue()
                bridge.changeAliasPermission(false)
                server.broadcastCapabilities(bridge.currentPermissionSnapshot())
                check(readControl(main.socket).type == "capabilities")
                checkPortClosed(changing.port)
                bridge.changeAliasPermission(true)
                server.broadcastCapabilities(bridge.currentPermissionSnapshot())
                main.revision = SrapCodec.parseCapabilitiesBody(readControl(main.socket).body).capabilityRevision
                val orphaned = main.issue()
                main.close()
                waitClosed()
                checkPortClosed(orphaned.port)
            }
            Main(tls(port)).use { main ->
                val grant = main.issue()
                server.disconnectAllSessions("lan_changed")
                val end = readControl(main.socket)
                check(text(end.body, "error") == "stale_session" && text(end.body, "reason") == "lan_changed")
                checkPortClosed(grant.port)
            }
            Main(tls(port)).use { main ->
                val grant = main.issue()
                check(server.revokePeer(windows.fingerprintSha256Upper))
                val end = readControl(main.socket)
                check(text(end.body, "error") == "permission_lost" && text(end.body, "reason") == "peer_revoked")
                checkPortClosed(grant.port)
                check(!server.revokePeer(windows.fingerprintSha256Upper))
            }
            tls(port).use { rejected ->
                send(rejected, SrapControlEnvelope("hello", SrapCodec.encodeHello()))
                check(readControl(rejected).type == "hello")
                check(text(readControl(rejected).body, "error") == "not_paired")
            }
            Socket(loopback, port).use { stalled ->
                stalled.soTimeout = 14_000
                tls(port, other).use { preHello ->
                    preHello.startHandshake()
                    preHello.soTimeout = 14_000
                    checkTcpClosed(stalled)
                    checkTcpClosed(preHello)
                }
            }
            Socket(loopback, port).use { stalled ->
                stalled.soTimeout = 3000
                server.close()
                checkTcpClosed(stalled)
            }
        }
    }
}
