package org.sonicroute.core

import java.net.InetAddress
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import javax.net.ssl.SSLSocket

/**
 * Executable PC-side SRAP v1.0 TLS test client (test-only, not shipped in APK).
 * Usage:
 *   args = [host, port, inviteUri, stage]
 *   stage = "pair_and_operate" (connects, tests bad-fp/version/secret, pairs, runs mTLS ops, waits for cap push & revoke)
 *         | "verify_revoked"   (uses saved client key or prints guidance)
 */
fun main(args: Array<String>) {
    require(args.size >= 3) { "Usage: SrapLiveDeviceClient <host> <port> <inviteUri> [waitSecondsForUiConfirm]" }
    val host = args[0]
    val port = args[1].toInt()
    val invite = SrapPairingInvite.parse(args[2])
    val waitForPushAndRevoke = args.getOrNull(3) == "watch_push_and_revoke"

    val kpg = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
    val kp = kpg.generateKeyPair()
    val cert = SrapCrypto.generateSelfSignedEcCertificate("CN=SonicRoute Live Test PC", kp.public, kp.private)
    val clientId = SrapServerIdentity(kp.private, cert)
    val androidFpBytes = invite.androidFingerprintSha256.chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    fun connect(expectedFp: String, forcedProto: String? = null, sendClientCert: Boolean = true): SSLSocket {
        val ctx = SrapTlsServer.buildClientSslContext(if (sendClientCert) clientId else null, expectedFp)
        val sock = ctx.socketFactory.createSocket(InetAddress.getByName(host), port) as SSLSocket
        sock.soTimeout = 20_000
        val allowed = if (forcedProto != null) arrayOf(forcedProto) else SrapCrypto.ALLOWED_TLS_PROTOCOLS
        sock.enabledProtocols = SrapCrypto.filterSupported(sock.supportedProtocols, allowed)
        val ciphers = SrapCrypto.filterSupported(sock.supportedCipherSuites, SrapCrypto.ALLOWED_TLS_CIPHERS)
        if (ciphers.isNotEmpty()) sock.enabledCipherSuites = ciphers
        sock.startHandshake()
        return sock
    }

    fun sendHello(sock: SSLSocket, minMin: Int = 0, minMax: Int = 0) {
        SrapCodec.writeFrame(
            sock.outputStream,
            SrapCodec.encodeControl(
                SrapControlEnvelope("hello", SrapCodec.encodeHello(1, minMin, minMax, "live-pc-test"))
            )
        )
    }

    fun readCtrl(sock: SSLSocket): SrapControlEnvelope =
        SrapCodec.parseControl(SrapCodec.readFrame(sock.inputStream) ?: error("Unexpected EOF"))

    fun req(sock: SSLSocket, sid: String, op: String, rev: Long, params: JsonObject): SrapControlEnvelope {
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
        val r = readCtrl(sock)
        check(r.type == "response" && r.id == id && r.session == sid) { "Unexpected reply for $op: $r" }
        return r
    }

    // 1. Wrong Android fingerprint must fail TLS pinning immediately.
    check(runCatching { connect("0".repeat(64)).close() }.isFailure)
    println("LIVE_PASS 1/8 wrong_fingerprint_rejected")

    // 2. Version mismatch rejected cleanly.
    connect(invite.androidFingerprintSha256).use { sock ->
        sendHello(sock, minMin = 2, minMax = 3)
        val err = readCtrl(sock)
        check(err.type == "event" && (err.body["error"] as JsonString).value == "version_mismatch")
    }
    println("LIVE_PASS 2/8 version_mismatch_rejected")

    // 3. Wrong secret rejected cleanly.
    connect(invite.androidFingerprintSha256).use { sock ->
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
        check(ch.type == "pair.challenge")
        val chBytes = SrapCrypto.base64UrlDecodeNoPad((ch.body["challenge"] as JsonString).value)
        val tr = SrapCrypto.buildPairingTranscript(invite.inviteId, chBytes, androidFpBytes, clientId.spkiSha256Bytes, 1, 0)
        val sig = SrapCrypto.signEcdsaP256Sha256(clientId.privateKey, tr)
        SrapCodec.writeFrame(
            sock.outputStream,
            SrapCodec.encodeControl(
                SrapControlEnvelope(
                    "pair.prove",
                    StrictJson.obj(
                        "inviteId" to StrictJson.str(invite.inviteIdB64),
                        "secret" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(ByteArray(16) { 0x11 })),
                        "publicKey" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(clientId.certificate.publicKey.encoded)),
                        "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(sig)),
                        "displayName" to StrictJson.str("BadSecretPC")
                    ),
                    id = UUID.randomUUID().toString().lowercase()
                )
            )
        )
        val res = readCtrl(sock)
        check(res.type == "pair.result" && (res.body["status"] as JsonString).value == "error")
    }
    println("LIVE_PASS 3/8 wrong_secret_rejected")

    // 4. Legitimate ECDSA P-256 pairing -> prints PAIRING_PENDING so test harness taps Confirm on device!
    connect(invite.androidFingerprintSha256).use { sock ->
        println("TLS_NEGOTIATED protocol=${sock.session.protocol} cipher=${sock.session.cipherSuite}")
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
        check(ch.type == "pair.challenge" && ch.id == bid)
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
                        "displayName" to StrictJson.str("LiveTest-WinPC")
                    ),
                    id = pid
                )
            )
        )
        val pending = readCtrl(sock)
        check(pending.type == "pair.result" && (pending.body["status"] as JsonString).value == "pending")
        println("PAIRING_PENDING fp=${clientId.fingerprintSha256Upper}")
        System.out.flush()
        val done = readCtrl(sock)
        check(done.type == "pair.result" && (done.body["status"] as JsonString).value == "done") {
            "Expected pairing done, got $done"
        }
    }
    println("LIVE_PASS 4/8 ecdsa_p256_pairing_confirmed")

    // 5. Mutual-TLS operational session over TLS 1.3 and TLS 1.2 + live audio/volume/route/catalog ops!
    for (proto in listOf("TLSv1.3", "TLSv1.2")) {
        connect(invite.androidFingerprintSha256, forcedProto = proto).use { sock ->
            check(sock.session.protocol == proto)
            sendHello(sock)
            check(readCtrl(sock).type == "hello")
            val capsEnv = readCtrl(sock)
            check(capsEnv.type == "capabilities")
            val sid = capsEnv.session!!
            var caps = SrapCodec.parseCapabilitiesBody(capsEnv.body)
            val catalogDeadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(15)
            while (caps.entries[Capability.APP_CATALOG]?.reason == "catalog_loading") {
                val remaining = (catalogDeadline - System.nanoTime()) / 1_000_000
                check(remaining > 0) { "Timed out waiting for the launcher catalog" }
                sock.soTimeout = remaining.toInt().coerceAtLeast(1)
                val update = readCtrl(sock)
                check(update.session == sid)
                if (update.type == "capabilities") caps = SrapCodec.parseCapabilitiesBody(update.body)
            }
            sock.soTimeout = 20_000
            check(caps.entries[Capability.APP_CATALOG]?.status == CapabilityStatus.AVAILABLE)
            val rev = caps.capabilityRevision
            check(caps.entries[Capability.ROUTE_OTHER_APPS]?.status != CapabilityStatus.AVAILABLE)
            check(caps.entries[Capability.VOLUME_OTHER_APPS]?.status != CapabilityStatus.AVAILABLE)
            check(caps.entries[Capability.EXCLUSIVE_CAPTURE]?.status != CapabilityStatus.AVAILABLE)

            val devRes = req(sock, sid, "audio.devices.list", rev, StrictJson.obj())
            check((devRes.body["status"] as JsonString).value == "done")
            val catRes = req(sock, sid, "catalog.list", rev, StrictJson.obj())
            check((catRes.body["status"] as JsonString).value == "done")
            val volSys = req(sock, sid, "volume.get", rev, StrictJson.obj("scope" to StrictJson.str("system_media")))
            check((volSys.body["status"] as JsonString).value == "done")

            if (proto == "TLSv1.3") {
                val playRes = req(
                    sock, sid, "audio.play.start", rev,
                    StrictJson.obj("streamId" to StrictJson.num(3), "targetKey" to StrictJson.str("current"))
                )
                check((playRes.body["status"] as JsonString).value == "done")
                val muteRes = req(
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
                val routedEv = readCtrl(sock)
                check(routedEv.type == "event" && (routedEv.body["name"] as JsonString).value == "audio.routed")
                check((routedEv.body["verified"] as JsonBool).value)
                check((routedEv.body["actualKey"] as JsonString).value == "speaker")

                val rawUidErr = req(
                    sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("other_app"), "uid" to StrictJson.num(10258), "percent" to StrictJson.num(50))
                )
                check((rawUidErr.body["error"] as JsonString).value == "invalid_argument")
                val shellErr = req(sock, sid, "shell.exec", rev, StrictJson.obj("cmd" to StrictJson.str("id")))
                check((shellErr.body["error"] as JsonString).value == "invalid_argument")

                // Stop stream cleanly
                val stopRes = req(sock, sid, "stream.stop", rev, StrictJson.obj("streamId" to StrictJson.num(3)))
                check((stopRes.body["status"] as JsonString).value == "done")
            }
            println("LIVE_PASS 5/8 mtls_${proto}_ops_and_verified_route_readback")
        }
    }

    // 6. Abrupt disconnect cleanup test: start owned muted track then drop TCP without stream.stop.
    connect(invite.androidFingerprintSha256).use { sock ->
        sendHello(sock)
        check(readCtrl(sock).type == "hello")
        val capsEnv = readCtrl(sock)
        val sid = capsEnv.session!!
        val rev = SrapCodec.parseCapabilitiesBody(capsEnv.body).capabilityRevision
        val playRes = req(
            sock, sid, "audio.play.start", rev,
            StrictJson.obj("streamId" to StrictJson.num(5), "targetKey" to StrictJson.str("speaker"))
        )
        check((playRes.body["status"] as JsonString).value == "done")
        req(
            sock, sid, "volume.set", rev,
            StrictJson.obj("scope" to StrictJson.str("owned_track"), "streamId" to StrictJson.num(5), "trackMuted" to StrictJson.bool(true))
        )
        // Abrupt close
    }
    println("LIVE_PASS 6/8 abrupt_disconnect_closed")

    if (waitForPushAndRevoke) {
        // 7. Hold mTLS session open, wait for backend switch capability push, then wait for peer revocation!
        connect(invite.androidFingerprintSha256).use { sock ->
            sock.soTimeout = 25_000
            sendHello(sock)
            check(readCtrl(sock).type == "hello")
            val initialCaps = readCtrl(sock)
            val initRev = SrapCodec.parseCapabilitiesBody(initialCaps.body).capabilityRevision
            println("MTLS_WATCH_READY rev=$initRev")
            System.out.flush()

            var gotCapPush = false
            var gotRevoke = false
            while (!gotRevoke) {
                val frame = SrapCodec.readFrame(sock.inputStream) ?: break
                val env = SrapCodec.parseControl(frame)
                if (env.type == "capabilities") {
                    val parsed = SrapCodec.parseCapabilitiesBody(env.body)
                    if (parsed.capabilityRevision > initRev) {
                        check(parsed.entries[Capability.ROUTE_OTHER_APPS]?.status != CapabilityStatus.AVAILABLE)
                        check(parsed.entries[Capability.VOLUME_OTHER_APPS]?.status != CapabilityStatus.AVAILABLE)
                        gotCapPush = true
                        println("LIVE_PASS 7/8 dynamic_capabilities_pushed rev=${parsed.capabilityRevision}")
                        System.out.flush()
                    }
                } else if (env.type == "event" && (env.body["error"] as? JsonString)?.value == "permission_lost") {
                    gotRevoke = true
                    println("LIVE_PASS 8a/8 live_revocation_event_received")
                    System.out.flush()
                }
            }
            check(gotCapPush) { "Did not receive dynamic capability push" }
            check(gotRevoke) { "Did not receive revocation event" }
        }

        // 8. Verify reconnect after revocation is rejected with not_paired!
        connect(invite.androidFingerprintSha256).use { sock ->
            sendHello(sock)
            check(readCtrl(sock).type == "hello")
            val err = readCtrl(sock)
            check(err.type == "event" && (err.body["error"] as JsonString).value == "not_paired")
        }
        println("LIVE_PASS 8b/8 post_revoke_reconnect_rejected")
    }
}
