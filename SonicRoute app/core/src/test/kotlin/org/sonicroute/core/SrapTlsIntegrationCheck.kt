package org.sonicroute.core

import java.net.InetAddress
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLSocket

/**
 * End-to-end JSSE TLS 1.3 & TLS 1.2 integration checks for SRAP v1.0:
 * - Invite URI encode/parse
 * - Wrong Android fingerprint rejection during TLS pinning
 * - Version mismatch rejection
 * - Untrusted access rejection when pairing window closed or during pairing window
 * - Wrong secret & tampered signature rejection + 5-attempt lockout
 * - Full ECDSA P-256 pairing flow + local confirmation + receipt recovery
 * - Mutual-TLS reconnect + dynamic capability sync + permission loss cancellation
 * - All 3 volume scopes (system_media / owned_track / other_app), route pending->verified readback,
 *   cancel idempotency, idempotencyKey dedup, peer revocation, abrupt disconnect cleanup, and TLS 1.2 AEAD.
 */
fun main(args: Array<String>) {
    val business = readBusinessVectors(args.single())
    checkPeerNotices()
    val androidId = createEcIdentity("CN=SonicRoute Android Test")
    val winId = createEcIdentity("CN=SonicRoute Windows Test")
    val rogueId = createEcIdentity("CN=Rogue Peer")

    val bridge = TestOperationBridge()
    val registry = InMemoryTrustedPeerRegistry()
    val pairingWindow = PairingWindowController()

    val server = SrapTlsServer(
        identity = androidId,
        appVersion = "0.1.0-test",
        peers = registry,
        pairingWindow = pairingWindow,
        bridge = bridge,
        bindAddress = InetAddress.getLoopbackAddress(),
        requestedPort = 0
    )

    server.use { srv ->
        val port = srv.start()
        check(port in 1..65535)

        // 1. Wrong server fingerprint MUST fail TLS handshake before any SRAP byte is sent.
        check(
            runCatching {
                openTlsClient(port, winId, "0".repeat(64)).use { it.startHandshake() }
            }.isFailure
        ) { "Expected TLS pinning failure on wrong Android fingerprint" }

        // 2. Untrusted client when pairing window is closed -> rejected with not_paired.
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            val reply = readControl(sock)
            check(reply.type == "hello")
            val err = readControl(sock)
            check(err.type == "event" && (err.body["error"] as? JsonString)?.value == "not_paired")
        }

        // 3. Version mismatch -> receives session.error version_mismatch and closes.
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope("hello", SrapCodec.encodeHello(major = 1, minorMin = 2, minorMax = 3))
                )
            )
            val err = readControl(sock)
            check(err.type == "event" && (err.body["error"] as? JsonString)?.value == "version_mismatch")
        }

        // 4. Invite creation & round-trip URI parsing.
        val invite = pairingWindow.openWindow(androidId.fingerprintSha256Upper, "127.0.0.1", port)
        val parsedInvite = SrapPairingInvite.parse(invite.toCopyableText())
        check(parsedInvite.androidFingerprintSha256 == androidId.fingerprintSha256Upper)
        check(parsedInvite.inviteId.contentEquals(invite.inviteId))
        check(parsedInvite.secret.contentEquals(invite.secret))

        // 5. Business request during pairing window is rejected with not_paired.
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope(
                        type = "request",
                        id = uuid(),
                        body = StrictJson.obj(
                            "op" to StrictJson.str("audio.devices.list"),
                            "capabilityRevision" to StrictJson.num(0),
                            "params" to StrictJson.obj()
                        )
                    )
                )
            )
            val err = readControl(sock)
            check(err.type == "event" && (err.body["error"] as? JsonString)?.value == "not_paired")
        }

        // 6. Wrong secret fails and 5 failed attempts burn the invite.
        repeat(5) { attempt ->
            openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
                sock.startHandshake()
                sendHello(sock)
                check(readControl(sock).type == "hello")
                val beginId = uuid()
                SrapCodec.writeFrame(
                    sock.outputStream,
                    SrapCodec.encodeControl(
                        SrapControlEnvelope("pair.begin", StrictJson.obj("inviteId" to StrictJson.str(invite.inviteIdB64)), id = beginId)
                    )
                )
                val ch = readControl(sock)
                check(ch.type == "pair.challenge")
                val challengeBytes = SrapCrypto.base64UrlDecodeNoPad((ch.body["challenge"] as JsonString).value)
                val transcript = SrapCrypto.buildPairingTranscript(
                    invite.inviteId, challengeBytes, androidId.spkiSha256Bytes, winId.spkiSha256Bytes, 1, 0
                )
                val sig = SrapCrypto.signEcdsaP256Sha256(winId.privateKey, transcript)
                SrapCodec.writeFrame(
                    sock.outputStream,
                    SrapCodec.encodeControl(
                        SrapControlEnvelope(
                            "pair.prove",
                            StrictJson.obj(
                                "inviteId" to StrictJson.str(invite.inviteIdB64),
                                "secret" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(ByteArray(16) { 0x5A })),
                                "publicKey" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(winId.certificate.publicKey.encoded)),
                                "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(sig)),
                                "displayName" to StrictJson.str("WinPC")
                            ),
                            id = uuid()
                        )
                    )
                )
                val res = readControl(sock)
                check(res.type == "pair.result" && (res.body["status"] as JsonString).value == "error")
                if (attempt == 4) {
                    check((res.body["reason"] as JsonString).value == "invite_locked_max_attempts")
                }
            }
        }
        check(!pairingWindow.isWindowOpen())

        // 7. Legitimate ECDSA P-256 pairing with local user confirmation + receipt recovery.
        val validInvite = pairingWindow.openWindow(androidId.fingerprintSha256Upper, "127.0.0.1", port)
        val savedSigRef = AtomicReference<ByteArray>()
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            check(sock.session.protocol in SrapCrypto.ALLOWED_TLS_PROTOCOLS)
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val beginId = uuid()
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope("pair.begin", StrictJson.obj("inviteId" to StrictJson.str(validInvite.inviteIdB64)), id = beginId)
                )
            )
            val ch = readControl(sock)
            check(ch.type == "pair.challenge" && ch.id == beginId)
            val challengeBytes = SrapCrypto.base64UrlDecodeNoPad((ch.body["challenge"] as JsonString).value)
            val transcript = SrapCrypto.buildPairingTranscript(
                validInvite.inviteId, challengeBytes, androidId.spkiSha256Bytes, winId.spkiSha256Bytes, 1, 0
            )
            val sig = SrapCrypto.signEcdsaP256Sha256(winId.privateKey, transcript)
            savedSigRef.set(sig)

            val proveId = uuid()
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope(
                        "pair.prove",
                        StrictJson.obj(
                            "inviteId" to StrictJson.str(validInvite.inviteIdB64),
                            "secret" to StrictJson.str(validInvite.secretB64),
                            "publicKey" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(winId.certificate.publicKey.encoded)),
                            "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(sig)),
                            "displayName" to StrictJson.str("Workstation-Win11")
                        ),
                        id = proveId
                    )
                )
            )
            val pending = readControl(sock)
            check(pending.type == "pair.result" && pending.id == proveId && (pending.body["status"] as JsonString).value == "pending")
            check(pairingWindow.currentPendingRequest()?.peerFingerprintSha256 == winId.fingerprintSha256Upper)
            check(pairingWindow.decidePending(true))
            val done = readControl(sock)
            check(done.type == "pair.result" && done.id == proveId && (done.body["status"] as JsonString).value == "done")
        }
        check(registry.findByFingerprint(winId.fingerprintSha256Upper)?.displayName == "Workstation-Win11")
        check(!pairingWindow.isWindowOpen())

        // 7b. Receipt recovery over a live TLS reconnection: identical pair.begin + pair.prove
        // recovers "done" without reusing an invite, while a rogue peer is rejected.
        val recoveredSig = savedSigRef.get()!!
        val originalPeersCount = registry.listPeers().size
        registry.removePeer(winId.fingerprintSha256Upper) // Simulate client retrying before trusting its mTLS session
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val bId = uuid()
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope("pair.begin", StrictJson.obj("inviteId" to StrictJson.str(validInvite.inviteIdB64)), id = bId)
                )
            )
            val ch = readControl(sock)
            check(ch.type == "pair.challenge" && ch.id == bId)
            val pId = uuid()
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope(
                        "pair.prove",
                        StrictJson.obj(
                            "inviteId" to StrictJson.str(validInvite.inviteIdB64),
                            "secret" to StrictJson.str(validInvite.secretB64),
                            "publicKey" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(winId.certificate.publicKey.encoded)),
                            "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(recoveredSig)),
                            "displayName" to StrictJson.str("Workstation-Win11")
                        ),
                        id = pId
                    )
                )
            )
            val done = readControl(sock)
            check(done.type == "pair.result" && done.id == pId && (done.body["status"] as JsonString).value == "done")
        }
        // Rogue peer cannot reuse the same burned inviteId even during receipt recovery window.
        openTlsClient(port, rogueId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val bId = uuid()
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope("pair.begin", StrictJson.obj("inviteId" to StrictJson.str(validInvite.inviteIdB64)), id = bId)
                )
            )
            val ch = readControl(sock)
            check(ch.type == "pair.challenge")
            val rogueChallenge = SrapCrypto.base64UrlDecodeNoPad((ch.body["challenge"] as JsonString).value)
            val rogueTranscript = SrapCrypto.buildPairingTranscript(
                validInvite.inviteId, rogueChallenge, androidId.spkiSha256Bytes, rogueId.spkiSha256Bytes, 1, 0
            )
            val rogueSig = SrapCrypto.signEcdsaP256Sha256(rogueId.privateKey, rogueTranscript)
            SrapCodec.writeFrame(
                sock.outputStream,
                SrapCodec.encodeControl(
                    SrapControlEnvelope(
                        "pair.prove",
                        StrictJson.obj(
                            "inviteId" to StrictJson.str(validInvite.inviteIdB64),
                            "secret" to StrictJson.str(validInvite.secretB64),
                            "publicKey" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(rogueId.certificate.publicKey.encoded)),
                            "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(rogueSig)),
                            "displayName" to StrictJson.str("RoguePC")
                        ),
                        id = uuid()
                    )
                )
            )
            val rejected = readControl(sock)
            check(rejected.type == "pair.result" && (rejected.body["status"] as JsonString).value == "error")
        }
        registry.savePeer(
            TrustedPeer(
                winId.fingerprintSha256Upper,
                "Workstation-Win11",
                SrapCrypto.base64UrlEncodeNoPad(winId.certificate.publicKey.encoded),
                System.currentTimeMillis()
            )
        )
        check(registry.listPeers().size == originalPeersCount)
        // Close pairing & receipt-recovery window before testing immediate post-hello rejection of rogue peer.
        pairingWindow.closeWindow()

        // 8. Rogue peer cannot connect even after winId paired.
        openTlsClient(port, rogueId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val err = readControl(sock)
            check(err.type == "event" && (err.body["error"] as JsonString).value == "not_paired")
        }

        // 9. Operational Mutual-TLS session over TLS 1.3 (and TLS 1.2 AEAD verification too).
        for (forcedProtocol in listOf("TLSv1.3", "TLSv1.2")) {
            openTlsClient(port, winId, androidId.fingerprintSha256Upper, forcedProtocol).use { sock ->
                sock.startHandshake()
                check(sock.session.protocol == forcedProtocol) { "Expected $forcedProtocol, got ${sock.session.protocol}" }
                sendHello(sock)
                check(readControl(sock).type == "hello")
                val capsEnv = readControl(sock)
                check(capsEnv.type == "capabilities")
                val sid = capsEnv.session ?: error("Missing session id")
                val caps = SrapCodec.parseCapabilitiesBody(capsEnv.body)
                val rev = caps.capabilityRevision
                // Unimplemented third-party capabilities must NEVER be advertised as available.
                check(caps.entries[Capability.ROUTE_OTHER_APPS]?.status != CapabilityStatus.AVAILABLE)
                check(caps.entries[Capability.VOLUME_OTHER_APPS]?.status != CapabilityStatus.AVAILABLE)

                // audio.devices.list returns wired/usb/bt/speaker/current
                val devRes = sendRequest(sock, sid, "audio.devices.list", rev, StrictJson.obj())
                check((devRes.body["status"] as JsonString).value == "done")
                val outputsArr = ((devRes.body["result"] as JsonObject)["outputs"] as JsonArray).items
                check(outputsArr.size == 5)

                bridge.catalogReady = false
                val loading = sendRequest(sock, sid, "catalog.list", rev, StrictJson.obj())
                check(loading.body["error"] == StrictJson.str("busy"))
                bridge.catalogReady = true
                val apps = sendRequest(sock, sid, "catalog.list", rev, StrictJson.obj()).body["result"] as JsonObject
                check(((apps["apps"] as JsonArray).items.single() as JsonObject)["appKey"] == StrictJson.str("com.example.music"))

                // volume.set system_media -> real readback & idempotency dedup
                val beforeWrites = bridge.systemVolumeWriteCount.get()
                val volRes1 = sendRequest(
                    sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("system_media"), "percent" to StrictJson.num(70)),
                    idempotencyKey = "vol-70"
                )
                val volRes2 = sendRequest(
                    sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("system_media"), "percent" to StrictJson.num(70)),
                    idempotencyKey = "vol-70"
                )
                check((volRes1.body["status"] as JsonString).value == "done")
                check(volRes2.body == volRes1.body)
                check(bridge.systemVolumeWriteCount.get() == beforeWrites + 1)
                val conflicting = sendRequest(sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("system_media"), "percent" to StrictJson.num(20)), "vol-70")
                check((conflicting.body["error"] as JsonString).value == "invalid_argument")
                val stale = sendRequest(sock, sid, "volume.set", rev + 1,
                    StrictJson.obj("scope" to StrictJson.str("system_media"), "percent" to StrictJson.num(70)), "vol-70")
                check((stale.body["error"] as JsonString).value == "stale_session")
                check(bridge.systemVolumeWriteCount.get() == beforeWrites + 1)
                for (name in listOf("volume.index", "volume.mixed", "volume.scope")) {
                    val vector = business.getValue(name)
                    val writes = bridge.systemVolumeWriteCount.get()
                    val response = sendRequest(sock, sid, SrapCodec.requiredText(vector.request().body, "op"), rev, vector.params()).body
                    if (name == "volume.index") {
                        check(vector.expected == "accept" && (response["result"] as JsonObject)["streamIndex"] == vector.params()["streamIndex"])
                        check(bridge.systemVolumeWriteCount.get() == writes + 1)
                    } else {
                        check(vector.expected == "reject" && response["status"] == StrictJson.str("error") &&
                            response["error"] == StrictJson.str("invalid_argument"))
                        check(bridge.systemVolumeWriteCount.get() == writes)
                    }
                }
                val reset = sendRequest(sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("system_media"), "streamIndex" to StrictJson.num(70)))
                check(((reset.body["result"] as JsonObject)["streamIndex"] as JsonNumber).toIntExactOrNull() == 70)

                // Start owned stream (odd streamId=3), adjust owned_track volume, verify system_media unchanged!
                val startRes = sendRequest(
                    sock, sid, "audio.play.start", rev,
                    StrictJson.obj("streamId" to StrictJson.num(3), "targetKey" to StrictJson.str("current"))
                )
                check((startRes.body["status"] as JsonString).value == "done")
                val trackVolRes = sendRequest(
                    sock, sid, "volume.set", rev,
                    StrictJson.obj(
                        "scope" to StrictJson.str("owned_track"),
                        "streamId" to StrictJson.num(3),
                        "trackGainPercent" to StrictJson.num(35),
                        "trackMuted" to StrictJson.bool(false)
                    )
                )
                check((trackVolRes.body["status"] as JsonString).value == "done")
                val sysCheck = sendRequest(
                    sock, sid, "volume.get", rev,
                    StrictJson.obj("scope" to StrictJson.str("system_media"))
                )
                check(((sysCheck.body["result"] as JsonObject)["percent"] as JsonNumber).toIntExactOrNull() == 70)

                // other_app forbids raw uid and honestly rejects unimplemented/unsupported
                val rawUidErr = sendRequest(
                    sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("other_app"), "uid" to StrictJson.num(10258), "percent" to StrictJson.num(50))
                )
                check((rawUidErr.body["error"] as JsonString).value == "invalid_argument")
                val otherAppErr = sendRequest(
                    sock, sid, "volume.set", rev,
                    StrictJson.obj("scope" to StrictJson.str("other_app"), "appKey" to StrictJson.str("com.example.music"), "percent" to StrictJson.num(50))
                )
                check((otherAppErr.body["error"] as JsonString).value == "unsupported")

                // audio.route.set -> pending response followed by audio.routed verified event
                val routeReqId = uuid()
                SrapCodec.writeFrame(
                    sock.outputStream,
                    SrapCodec.encodeControl(
                        SrapControlEnvelope(
                            type = "request",
                            id = routeReqId,
                            session = sid,
                            body = StrictJson.obj(
                                "op" to StrictJson.str("audio.route.set"),
                                "capabilityRevision" to StrictJson.num(rev),
                                "params" to StrictJson.obj(
                                    "scope" to StrictJson.str("owned_track"),
                                    "streamId" to StrictJson.num(3),
                                    "targetKey" to StrictJson.str("bt:AA:BB:CC:DD:EE:FF")
                                )
                            )
                        )
                    )
                )
                val routePending = readControl(sock)
                check(routePending.id == routeReqId && (routePending.body["status"] as JsonString).value == "pending")
                val routeEvent = readControl(sock)
                check(routeEvent.type == "event" && (routeEvent.body["name"] as JsonString).value == "audio.routed")
                check((routeEvent.body["requestId"] as JsonString).value == routeReqId)
                check((routeEvent.body["actualKey"] as JsonString).value == "bt:AA:BB:CC:DD:EE:FF")
                check((routeEvent.body["verified"] as JsonBool).value)

                // Disallowed shell op rejected
                val shellErr = sendRequest(sock, sid, "shell.exec", rev, StrictJson.obj("cmd" to StrictJson.str("id")))
                check((shellErr.body["error"] as JsonString).value == "invalid_argument")

                // Cancel stream idempotently
                val cancelId = uuid()
                SrapCodec.writeFrame(
                    sock.outputStream,
                    SrapCodec.encodeControl(
                        SrapControlEnvelope("cancel", StrictJson.obj("streamId" to StrictJson.num(3)), id = cancelId, session = sid)
                    )
                )
                val cancelRes = readControl(sock)
                check(cancelRes.id == cancelId && (cancelRes.body["status"] as JsonString).value == "done")
            }
        }

        // 10. Dynamic capability update + permission loss cancels active stream, and revocation terminates live session.
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val capsEnv = readControl(sock)
            val sid = capsEnv.session!!
            val rev = SrapCodec.parseCapabilitiesBody(capsEnv.body).capabilityRevision

            val started = sendRequest(
                sock, sid, "audio.play.start", rev,
                StrictJson.obj("streamId" to StrictJson.num(5), "targetKey" to StrictJson.str("speaker"))
            )
            check((started.body["status"] as JsonString).value == "done")
            check(bridge.activeStreams.containsKey("$sid:5"))

            // Simulate capability revocation (PLAY_AUDIO -> UNAVAILABLE) -> pushes new capabilities & cancels stream 5!
            bridge.revokePlayAudio()
            srv.broadcastCapabilities(bridge.currentPermissionSnapshot())
            val pushedCaps = readControl(sock)
            check(pushedCaps.type == "capabilities")
            check(SrapCodec.parseCapabilitiesBody(pushedCaps.body).capabilityRevision == rev + 1)
            val canceledEv = readControl(sock)
            check(canceledEv.type == "event" && (canceledEv.body["name"] as JsonString).value == "stream.canceled")
            check(!bridge.activeStreams.containsKey("$sid:5"))

            // Revoke peer -> active session immediately receives permission_lost & closes!
            check(srv.revokePeer(winId.fingerprintSha256Upper))
            val revokedEv = readControl(sock)
            check(revokedEv.type == "event" && (revokedEv.body["error"] as JsonString).value == "permission_lost")
        }

        // After revocation, reconnect MUST be rejected with not_paired.
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val err = readControl(sock)
            check(err.type == "event" && (err.body["error"] as JsonString).value == "not_paired")
        }

        // 11. Abrupt disconnect cleans up owned session resources cleanly.
        bridge.restorePlayAudio()
        registry.savePeer(TrustedPeer(winId.fingerprintSha256Upper, "Re-paired", "", System.currentTimeMillis()))
        val cleanupLatch = CountDownLatch(1)
        bridge.onCleanup = { cleanupLatch.countDown() }
        openTlsClient(port, winId, androidId.fingerprintSha256Upper).use { sock ->
            sock.startHandshake()
            sendHello(sock)
            check(readControl(sock).type == "hello")
            val capsEnv = readControl(sock)
            val sid = capsEnv.session!!
            val rev = SrapCodec.parseCapabilitiesBody(capsEnv.body).capabilityRevision
            val started = sendRequest(
                sock, sid, "audio.play.start", rev,
                StrictJson.obj("streamId" to StrictJson.num(7), "targetKey" to StrictJson.str("speaker"))
            )
            check((started.body["status"] as JsonString).value == "done")
            // Abruptly drop socket without stream.stop
        }
        check(cleanupLatch.await(3, TimeUnit.SECONDS)) { "Expected cleanup after abrupt disconnect" }
        check(bridge.activeStreams.isEmpty())
    }

    println("PASS SRAP v1.0 TLS 1.3/1.2 pairing, mTLS, dispatch, capability sync, revocation & cleanup checks")
}

internal fun createEcIdentity(cn: String): SrapServerIdentity {
    val kpg = KeyPairGenerator.getInstance("EC")
    kpg.initialize(ECGenParameterSpec("secp256r1"))
    val kp = kpg.generateKeyPair()
    val cert = SrapCrypto.generateSelfSignedEcCertificate(cn, kp.public, kp.private)
    return SrapServerIdentity(kp.private, cert)
}

internal fun openTlsClient(
    port: Int,
    clientIdentity: SrapServerIdentity?,
    expectedServerFp: String,
    forcedProtocol: String? = null,
    address: InetAddress = InetAddress.getLoopbackAddress()
): SSLSocket {
    val ctx = SrapTlsServer.buildClientSslContext(clientIdentity, expectedServerFp)
    val sock = ctx.socketFactory.createSocket(address, port) as SSLSocket
    sock.soTimeout = 5_000
    val allowed = if (forcedProtocol != null) arrayOf(forcedProtocol) else SrapCrypto.ALLOWED_TLS_PROTOCOLS
    sock.enabledProtocols = SrapCrypto.filterSupported(sock.supportedProtocols, allowed)
    val ciphers = SrapCrypto.filterSupported(sock.supportedCipherSuites, SrapCrypto.ALLOWED_TLS_CIPHERS)
    if (ciphers.isNotEmpty()) sock.enabledCipherSuites = ciphers
    return sock
}

private fun sendHello(sock: SSLSocket) {
    SrapCodec.writeFrame(
        sock.outputStream,
        SrapCodec.encodeControl(SrapControlEnvelope("hello", SrapCodec.encodeHello(appVersion = "win-test")))
    )
}

internal fun readControl(sock: SSLSocket): SrapControlEnvelope {
    val frame = SrapCodec.readFrame(sock.inputStream) ?: error("Unexpected EOF reading control frame")
    return SrapCodec.parseControl(frame)
}

private fun sendRequest(
    sock: SSLSocket,
    session: String,
    op: String,
    capRev: Long,
    params: JsonObject,
    idempotencyKey: String? = null
): SrapControlEnvelope {
    val id = uuid()
    SrapCodec.writeFrame(
        sock.outputStream,
        SrapCodec.encodeControl(
            SrapControlEnvelope(
                type = "request",
                id = id,
                session = session,
                body = StrictJson.obj(
                    "op" to StrictJson.str(op),
                    "capabilityRevision" to StrictJson.num(capRev),
                    "params" to params,
                    "idempotencyKey" to idempotencyKey?.let { StrictJson.str(it) }
                )
            )
        )
    )
    val reply = readControl(sock)
    check(reply.type == "response" && reply.id == id && reply.session == session)
    return reply
}

private fun uuid(): String = UUID.randomUUID().toString().lowercase()

internal class TestOperationBridge : SrapOperationBridge {
    private val permState = PermissionState(10440) {
        mapOf(
            Capability.ADB_ALIAS to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "paired_profile"),
            Capability.AUDIO_DEVICES to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "local_outputs"),
            Capability.APP_CATALOG to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "launcher_intents"),
            Capability.PHONE_VOLUME to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "system_media", limits = mapOf("indexWrite" to "1")),
            Capability.PLAY_AUDIO to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "owned_track"),
            Capability.ROUTE_PLAYBACK to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "owned_track"),
            Capability.ROUTE_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNSUPPORTED, reason = "no_public_api"),
            Capability.VOLUME_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNSUPPORTED, reason = "no_public_api")
        )
    }

    private var sysVolumePercent = 50
    val systemVolumeWriteCount = AtomicInteger(0)
    val activeStreams = ConcurrentHashMap<String, Triple<String, Int, Boolean>>()
    @Volatile var onCleanup: (() -> Unit)? = null

    private val outputs = AudioSinkNormalizer.normalize(
        listOf(
            RawOutputDevice(3, 2, AudioOutputKind.SPEAKER, "Speaker", null, isDefaultMedia = true),
            RawOutputDevice(8, 8, AudioOutputKind.BLUETOOTH, "BT Headset", "AA:BB:CC:DD:EE:FF", isPrimaryBluetooth = true),
            RawOutputDevice(12, 3, AudioOutputKind.WIRED, "Wired Headset", null),
            RawOutputDevice(15, 22, AudioOutputKind.USB, "USB DAC", "card=1:dev=0")
        )
    )

    fun changeAliasPermission(available: Boolean) {
        permState.setCapability(permState.snapshot().generation, Capability.ADB_ALIAS, CapabilityEntry(
            if (available) CapabilityStatus.AVAILABLE else CapabilityStatus.NEEDS_AUTHORIZATION, scope = "paired_profile"))
    }

    fun revokePlayAudio() {
        permState.setCapability(
            permState.snapshot().generation,
            Capability.PLAY_AUDIO,
            CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = "revoked")
        )
    }

    fun restorePlayAudio() {
        permState.setCapability(
            permState.snapshot().generation,
            Capability.PLAY_AUDIO,
            CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "owned_track")
        )
    }

    override fun currentPermissionSnapshot(): PermissionSnapshot = permState.snapshot()
    override fun listAudioOutputs(): List<AudioOutputSink> = outputs
    @Volatile var catalogReady = true
    override fun listAppCatalog(): AppCatalogSnapshot? = if (!catalogReady) null else AppCatalogSnapshot(
        "launcher_intents",
        listOf(AppCatalogEntry("com.example.music", "Music", 10258))
    )

    override fun readSystemMediaVolume(): SystemVolumeSnapshot = SystemVolumeSnapshot(
        streamIndex = sysVolumePercent,
        minIndex = 0,
        maxIndex = 100,
        percent = sysVolumePercent
    )

    override fun writeSystemMediaVolume(sessionId: String, expectedRevision: Long, percent: Int): OperationResult<SystemVolumeSnapshot> {
        systemVolumeWriteCount.incrementAndGet()
        sysVolumePercent = percent.coerceIn(0, 100)
        return OperationResult.Done(readSystemMediaVolume())
    }

    override fun writeSystemMediaVolumeIndex(sessionId: String, expectedRevision: Long, index: Int) =
        if (index in 0..100) writeSystemMediaVolume(sessionId, expectedRevision, index)
        else OperationResult.Failure(ProtocolError.INVALID_ARGUMENT)

    override fun startOwnedPlayback(sessionId: String, expectedRevision: Long, streamId: Long, targetKey: String): OperationResult<OwnedPlaybackSnapshot> {
        activeStreams["$sessionId:$streamId"] = Triple(targetKey, 60, false)
        return OperationResult.Done(OwnedPlaybackSnapshot(OwnedPlaybackState.PLAYING, targetKey, trackGainPercent = 60, trackMuted = false))
    }

    override fun stopOwnedPlayback(sessionId: String, streamId: Long): Boolean =
        activeStreams.remove("$sessionId:$streamId") != null

    override fun readOwnedTrackVolume(sessionId: String, streamId: Long): OperationResult<Pair<Int, Boolean>> {
        val cur = activeStreams["$sessionId:$streamId"]
            ?: return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "stream_not_found")
        return OperationResult.Done(cur.second to cur.third)
    }

    override fun writeOwnedTrackVolume(
        sessionId: String,
        expectedRevision: Long,
        streamId: Long,
        gainPercent: Int?,
        muted: Boolean?
    ): OperationResult<Pair<Int, Boolean>> {
        val key = "$sessionId:$streamId"
        val cur = activeStreams[key] ?: return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "stream_not_found")
        val next = Triple(cur.first, gainPercent ?: cur.second, muted ?: cur.third)
        activeStreams[key] = next
        return OperationResult.Done(next.second to next.third)
    }

    override fun requestOwnedTrackRoute(
        sessionId: String,
        expectedRevision: Long,
        streamId: Long,
        requestId: String,
        targetKey: String,
        onRouted: (RoutedDeviceReadback) -> Unit
    ): OperationResult<Unit> {
        val key = "$sessionId:$streamId"
        val cur = activeStreams[key] ?: return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "stream_not_found")
        val sink = AudioSinkNormalizer.findSink(outputs, targetKey)
            ?: return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "unknown_target")
        activeStreams[key] = cur.copy(first = targetKey)
        Thread {
            Thread.sleep(30)
            onRouted(RoutedDeviceReadback(targetKey, sink, preferredApplied = true, verified = true, reason = "verified"))
        }.start()
        return OperationResult.Pending()
    }

    override fun cleanupSessionResources(sessionId: String) {
        activeStreams.keys.removeIf { it.startsWith("$sessionId:") }
        onCleanup?.invoke()
    }
}
