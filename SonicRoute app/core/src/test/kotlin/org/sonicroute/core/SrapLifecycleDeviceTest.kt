package org.sonicroute.core

import java.io.File
import java.net.InetAddress
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.security.spec.PKCS8EncodedKeySpec
import java.util.UUID
import javax.net.ssl.SSLSocket

/**
 * Test-only PC client for verifying background survival, app switching, screen lock,
 * notification stop, and post-process-reclaim mTLS reconnection without re-pairing.
 *
 * Modes:
 *   pair <host> <port> <inviteUri> <keyDir>
 *   mtls_session <host> <port> <androidFp> <keyDir> <triggerFile>
 *   mtls_probe_once <host> <port> <androidFp> <keyDir>
 */
fun main(args: Array<String>) {
    require(args.isNotEmpty()) { "Missing mode" }
    when (args[0]) {
        "pair" -> runPair(args[1], args[2].toInt(), SrapPairingInvite.parse(args[3]), File(args[4]))
        "mtls_session" -> runMtlsSession(args[1], args[2].toInt(), args[3], File(args[4]), File(args[5]))
        "mtls_probe_once" -> runMtlsProbeOnce(args[1], args[2].toInt(), args[3], File(args[4]))
        else -> error("Unknown mode: ${args[0]}")
    }
}

private fun loadOrCreateClientIdentity(dir: File): SrapServerIdentity {
    dir.mkdirs()
    val keyFile = File(dir, "client.pk8")
    val certFile = File(dir, "client.der")
    if (keyFile.isFile && certFile.isFile) {
        val priv = KeyFactory.getInstance("EC").generatePrivate(PKCS8EncodedKeySpec(keyFile.readBytes()))
        val cert = CertificateFactory.getInstance("X.509")
            .generateCertificate(certFile.inputStream()) as X509Certificate
        return SrapServerIdentity(priv, cert)
    }
    val kpg = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
    val kp = kpg.generateKeyPair()
    val cert = SrapCrypto.generateSelfSignedEcCertificate("CN=SonicRoute Lifecycle PC", kp.public, kp.private)
    keyFile.writeBytes(kp.private.encoded)
    certFile.writeBytes(cert.encoded)
    return SrapServerIdentity(kp.private, cert)
}

private fun openMtls(host: String, port: Int, clientId: SrapServerIdentity, expectedAndroidFp: String): SSLSocket {
    val ctx = SrapTlsServer.buildClientSslContext(clientId, expectedAndroidFp)
    val sock = ctx.socketFactory.createSocket(InetAddress.getByName(host), port) as SSLSocket
    sock.soTimeout = 25_000
    sock.enabledProtocols = SrapCrypto.filterSupported(sock.supportedProtocols, SrapCrypto.ALLOWED_TLS_PROTOCOLS)
    val ciphers = SrapCrypto.filterSupported(sock.supportedCipherSuites, SrapCrypto.ALLOWED_TLS_CIPHERS)
    if (ciphers.isNotEmpty()) sock.enabledCipherSuites = ciphers
    sock.startHandshake()
    return sock
}

private fun sendHello(sock: SSLSocket) {
    SrapCodec.writeFrame(
        sock.outputStream,
        SrapCodec.encodeControl(SrapControlEnvelope("hello", SrapCodec.encodeHello(1, 0, 0, "lifecycle-pc")))
    )
}

private fun readCtrl(sock: SSLSocket): SrapControlEnvelope =
    SrapCodec.parseControl(SrapCodec.readFrame(sock.inputStream) ?: error("Unexpected EOF"))

private fun sendReq(sock: SSLSocket, sid: String, op: String, rev: Long, params: JsonObject): SrapControlEnvelope {
    val id = UUID.randomUUID().toString().lowercase()
    SrapCodec.writeFrame(
        sock.outputStream,
        SrapCodec.encodeControl(
            SrapControlEnvelope(
                "request",
                StrictJson.obj(
                    "op" to StrictJson.str(op),
                    "capabilityRevision" to StrictJson.num(rev),
                    "params" to params
                ),
                id = id,
                session = sid
            )
        )
    )
    while (true) {
        val env = readCtrl(sock)
        if (env.type == "response" && env.id == id) return env
    }
}

private fun runPair(host: String, port: Int, invite: SrapPairingInvite, keyDir: File) {
    val clientId = loadOrCreateClientIdentity(keyDir)
    val androidFpBytes = invite.androidFingerprintSha256.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    openMtls(host, port, clientId, invite.androidFingerprintSha256).use { sock ->
        sendHello(sock)
        check(readCtrl(sock).type == "hello")
        val bid = UUID.randomUUID().toString().lowercase()
        SrapCodec.writeFrame(
            sock.outputStream,
            SrapCodec.encodeControl(
                SrapControlEnvelope("pair.begin", StrictJson.obj("inviteId" to StrictJson.str(invite.inviteIdB64)), id = bid)
            )
        )
        val ch = readCtrl(sock)
        val chBytes = SrapCrypto.base64UrlDecodeNoPad((ch.body["challenge"] as JsonString).value)
        val tr = SrapCrypto.buildPairingTranscript(invite.inviteId, chBytes, androidFpBytes, clientId.spkiSha256Bytes, 1, 0)
        val sig = SrapCrypto.signEcdsaP256Sha256(clientId.privateKey, tr)
        val pid = UUID.randomUUID().toString().lowercase()
        SrapCodec.writeFrame(
            sock.outputStream,
            SrapCodec.encodeControl(
                SrapControlEnvelope(
                    "pair.prove",
                    StrictJson.obj(
                        "inviteId" to StrictJson.str(invite.inviteIdB64),
                        "secret" to StrictJson.str(invite.secretB64),
                        "publicKey" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(clientId.certificate.publicKey.encoded)),
                        "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(sig)),
                        "displayName" to StrictJson.str("Lifecycle-WinPC")
                    ),
                    id = pid
                )
            )
        )
        val pending = readCtrl(sock)
        check((pending.body["status"] as JsonString).value == "pending")
        println("PAIRING_PENDING")
        System.out.flush()
        val done = readCtrl(sock)
        check((done.body["status"] as JsonString).value == "done")
        println("PAIRING_DONE")
        System.out.flush()
    }
}

private fun runMtlsSession(host: String, port: Int, androidFp: String, keyDir: File, triggerFile: File) {
    val clientId = loadOrCreateClientIdentity(keyDir)
    openMtls(host, port, clientId, androidFp).use { sock ->
        sendHello(sock)
        check(readCtrl(sock).type == "hello")
        var capsEnv = readCtrl(sock)
        check(capsEnv.type == "capabilities")
        val sid = capsEnv.session!!
        var rev = SrapCodec.parseCapabilitiesBody(capsEnv.body).capabilityRevision
        println("MTLS_CONNECTED rev=$rev proto=${sock.session.protocol}")
        System.out.flush()

        // Wait until harness writes "PLAY_LOCKED" into triggerFile (after HOME -> Settings -> Screen Lock)
        waitForTrigger(triggerFile, "PLAY_LOCKED")

        // Drain any capability pushes that arrived while switching apps / locking screen
        sock.soTimeout = 300
        while (true) {
            val f = runCatching { SrapCodec.readFrame(sock.inputStream) }.getOrNull() ?: break
            val e = SrapCodec.parseControl(f)
            if (e.type == "capabilities") rev = SrapCodec.parseCapabilitiesBody(e.body).capabilityRevision
        }
        sock.soTimeout = 20_000

        val devRes = sendReq(sock, sid, "audio.devices.list", rev, StrictJson.obj())
        check((devRes.body["status"] as JsonString).value == "done")
        val volRes = sendReq(sock, sid, "volume.get", rev, StrictJson.obj("scope" to StrictJson.str("system_media")))
        check((volRes.body["status"] as JsonString).value == "done")
        val playRes = sendReq(
            sock, sid, "audio.play.start", rev,
            StrictJson.obj("streamId" to StrictJson.num(3), "targetKey" to StrictJson.str("speaker"))
        )
        check((playRes.body["status"] as JsonString).value == "done")
        val muteRes = sendReq(
            sock, sid, "volume.set", rev,
            StrictJson.obj("scope" to StrictJson.str("owned_track"), "streamId" to StrictJson.num(3), "trackMuted" to StrictJson.bool(true))
        )
        check((muteRes.body["status"] as JsonString).value == "done")

        val routeReqId = UUID.randomUUID().toString().lowercase()
        SrapCodec.writeFrame(
            sock.outputStream,
            SrapCodec.encodeControl(
                SrapControlEnvelope(
                    "request",
                    StrictJson.obj(
                        "op" to StrictJson.str("audio.route.set"),
                        "capabilityRevision" to StrictJson.num(rev),
                        "params" to StrictJson.obj(
                            "scope" to StrictJson.str("owned_track"),
                            "streamId" to StrictJson.num(3),
                            "targetKey" to StrictJson.str("speaker")
                        )
                    ),
                    id = routeReqId,
                    session = sid
                )
            )
        )
        val pending = readCtrl(sock)
        check(pending.type == "response" && (pending.body["status"] as JsonString).value == "pending")
        var routedVerified = false
        while (!routedVerified) {
            val ev = readCtrl(sock)
            if (ev.type == "event" && (ev.body["name"] as? JsonString)?.value == "audio.routed") {
                check((ev.body["verified"] as JsonBool).value)
                check((ev.body["actualKey"] as JsonString).value == "speaker")
                routedVerified = true
            }
        }
        println("LOCKED_SCREEN_AUDIO_VERIFIED")
        System.out.flush()

        // Wait until harness writes "STOP_AUDIO" (after sampling active-transfer CPU/mem)
        waitForTrigger(triggerFile, "STOP_AUDIO")
        val stopRes = sendReq(sock, sid, "stream.stop", rev, StrictJson.obj("streamId" to StrictJson.num(3)))
        check((stopRes.body["status"] as JsonString).value == "done")
        println("LOCKED_SCREEN_AUDIO_STOPPED")
        System.out.flush()

        // Wait until harness writes "DISCONNECT" (after sampling idle locked-screen mTLS CPU/mem)
        waitForTrigger(triggerFile, "DISCONNECT")
        println("MTLS_SESSION_DONE")
        System.out.flush()
    }
}

private fun runMtlsProbeOnce(host: String, port: Int, androidFp: String, keyDir: File) {
    val clientId = loadOrCreateClientIdentity(keyDir)
    openMtls(host, port, clientId, androidFp).use { sock ->
        sendHello(sock)
        check(readCtrl(sock).type == "hello")
        val capsEnv = readCtrl(sock)
        check(capsEnv.type == "capabilities" && capsEnv.session != null)
        val sid = capsEnv.session!!
        val rev = SrapCodec.parseCapabilitiesBody(capsEnv.body).capabilityRevision
        val devRes = sendReq(sock, sid, "audio.devices.list", rev, StrictJson.obj())
        check((devRes.body["status"] as JsonString).value == "done")
        println("MTLS_RECONNECT_NO_REPAIR_OK proto=${sock.session.protocol} rev=$rev")
        System.out.flush()
    }
}

private fun waitForTrigger(file: File, expected: String) {
    val deadline = System.currentTimeMillis() + 40_000L
    while (System.currentTimeMillis() < deadline) {
        if (file.isFile && file.readText().trim() == expected) return
        Thread.sleep(200)
    }
    error("Timed out waiting for trigger $expected")
}
