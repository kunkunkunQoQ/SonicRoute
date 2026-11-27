package org.sonicroute.core

import java.io.Closeable
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.Locale
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

data class TrustedPeer(
    val fingerprintSha256: String,
    val displayName: String,
    val publicKeyDerB64: String,
    val pairedAtMs: Long,
    val lastConnectedAtMs: Long = pairedAtMs
)

interface TrustedPeerRegistry {
    fun findByFingerprint(fingerprintSha256: String): TrustedPeer?
    fun listPeers(): List<TrustedPeer>
    fun savePeer(peer: TrustedPeer)
    fun removePeer(fingerprintSha256: String): Boolean
    fun touchLastConnected(fingerprintSha256: String, nowMs: Long)
}

class InMemoryTrustedPeerRegistry : TrustedPeerRegistry {
    private val peers = LinkedHashMap<String, TrustedPeer>()

    @Synchronized
    override fun findByFingerprint(fingerprintSha256: String): TrustedPeer? =
        peers[fingerprintSha256.uppercase(Locale.ROOT)]

    @Synchronized
    override fun listPeers(): List<TrustedPeer> = peers.values.toList()

    @Synchronized
    override fun savePeer(peer: TrustedPeer) {
        val norm = peer.copy(fingerprintSha256 = peer.fingerprintSha256.uppercase(Locale.ROOT))
        peers[norm.fingerprintSha256] = norm
    }

    @Synchronized
    override fun removePeer(fingerprintSha256: String): Boolean =
        peers.remove(fingerprintSha256.uppercase(Locale.ROOT)) != null

    @Synchronized
    override fun touchLastConnected(fingerprintSha256: String, nowMs: Long) {
        val key = fingerprintSha256.uppercase(Locale.ROOT)
        peers[key]?.let { peers[key] = it.copy(lastConnectedAtMs = nowMs) }
    }
}

data class PendingPairingRequest(
    val inviteIdB64: String,
    val peerFingerprintSha256: String,
    val displayName: String,
    val publicKeyDerB64: String
)

/**
 * Manages the time-bounded (120 s) one-time pairing window, max 5 failed attempts,
 * local user confirmation gate, and confirmed-receipt recovery without invite reuse.
 */
class PairingWindowController(
    private val clockMs: () -> Long = System::currentTimeMillis,
    private val onStateChanged: () -> Unit = {}
) {
    private val lock = Any()
    private var activeInvite: SrapPairingInvite? = null
    private var failedAttempts = 0
    private var pendingApproval: PendingApprovalSlot? = null
    private var lastConfirmedReceipt: ConfirmedReceipt? = null

    private data class ConfirmedReceipt(
        val inviteIdB64: String,
        val peerFingerprintSha256: String,
        val secretBytes: ByteArray,
        val challengeBytes: ByteArray,
        val signatureHex: String,
        val expiresAtMs: Long
    )

    private class PendingApprovalSlot(
        val request: PendingPairingRequest,
        val latch: CountDownLatch = CountDownLatch(1),
        @Volatile var approved: Boolean? = null
    )

    fun openWindow(
        androidFingerprintSha256: String,
        hostHint: String?,
        portHint: Int,
        ttlMs: Long = DEFAULT_WINDOW_TTL_MS
    ): SrapPairingInvite {
        val invite = SrapPairingInvite.create(
            androidFingerprintSha256 = androidFingerprintSha256,
            hostHint = hostHint,
            portHint = portHint,
            nowMs = clockMs(),
            ttlMs = ttlMs
        )
        synchronized(lock) {
            cancelPendingLocked()
            activeInvite = invite
            failedAttempts = 0
            lastConfirmedReceipt = null
        }
        onStateChanged()
        return invite
    }

    fun clearReceiptForPeer(fingerprintSha256: String) {
        val upper = fingerprintSha256.uppercase(Locale.ROOT)
        synchronized(lock) {
            if (lastConfirmedReceipt?.peerFingerprintSha256 == upper) {
                lastConfirmedReceipt = null
            }
        }
    }

    fun closeWindow() {
        synchronized(lock) {
            cancelPendingLocked()
            activeInvite = null
            failedAttempts = 0
            lastConfirmedReceipt = null
        }
        onStateChanged()
    }

    fun currentInvite(): SrapPairingInvite? = synchronized(lock) {
        val inv = activeInvite ?: return null
        if (clockMs() >= inv.expiresAtMs) {
            cancelPendingLocked()
            activeInvite = null
            return null
        }
        inv
    }

    fun isWindowOpen(): Boolean = currentInvite() != null

    fun hasActiveReceiptRecovery(): Boolean = synchronized(lock) {
        val r = lastConfirmedReceipt ?: return false
        clockMs() < r.expiresAtMs
    }

    fun currentPendingRequest(): PendingPairingRequest? = synchronized(lock) {
        pendingApproval?.request
    }

    fun decidePending(approve: Boolean): Boolean {
        synchronized(lock) {
            val slot = pendingApproval ?: return false
            val invite = activeInvite ?: return false
            if (invite.inviteIdB64 != slot.request.inviteIdB64 || clockMs() >= invite.expiresAtMs || slot.approved != null) return false
            slot.approved = approve
            slot.latch.countDown()
        }
        onStateChanged()
        return true
    }

    internal fun issueChallengeForBegin(inviteIdBytes: ByteArray): ByteArray? {
        val now = clockMs()
        synchronized(lock) {
            val inv = activeInvite
            if (inv != null && now < inv.expiresAtMs && SrapCrypto.constantTimeEquals(inv.inviteId, inviteIdBytes)) {
                return SrapCrypto.randomBytes(32)
            }
            // Receipt recovery returns the original challenge so the identical already-verified
            // pair.prove request can recover a lost done ACK without re-consuming an invite.
            val receipt = lastConfirmedReceipt
            if (receipt != null && now < receipt.expiresAtMs &&
                receipt.inviteIdB64 == SrapCrypto.base64UrlEncodeNoPad(inviteIdBytes)
            ) {
                return receipt.challengeBytes.copyOf()
            }
            return null
        }
    }

    internal fun checkReceiptRecovery(
        inviteIdB64: String,
        peerFingerprintSha256: String,
        secretBytes: ByteArray,
        signatureDer: ByteArray
    ): Boolean {
        val now = clockMs()
        val sigHex = SrapCrypto.toHexUpper(signatureDer)
        synchronized(lock) {
            val r = lastConfirmedReceipt ?: return false
            return now < r.expiresAtMs &&
                r.inviteIdB64 == inviteIdB64 &&
                r.peerFingerprintSha256 == peerFingerprintSha256 &&
                SrapCrypto.constantTimeEquals(r.secretBytes, secretBytes) &&
                r.signatureHex == sigHex
        }
    }

    internal fun recordFailureAndCheckBurned(): Boolean {
        var burned = false
        synchronized(lock) {
            failedAttempts++
            if (failedAttempts >= MAX_FAILED_ATTEMPTS) {
                activeInvite = null
                cancelPendingLocked()
                burned = true
            }
        }
        if (burned) onStateChanged()
        return burned
    }

    internal fun awaitLocalConfirmation(
        request: PendingPairingRequest,
        timeoutMs: Long,
        onPendingCreated: () -> Unit
    ): Boolean? {
        val slot = PendingApprovalSlot(request)
        synchronized(lock) {
            val inv = activeInvite ?: return false
            if (inv.inviteIdB64 != request.inviteIdB64 || clockMs() >= inv.expiresAtMs || pendingApproval != null) return null
            pendingApproval = slot
        }
        onStateChanged()
        onPendingCreated()
        val waitMs = timeoutMs.coerceIn(1_000L, DEFAULT_WINDOW_TTL_MS)
        val signaled = runCatching { slot.latch.await(waitMs, TimeUnit.MILLISECONDS) }.getOrDefault(false)
        synchronized(lock) {
            if (pendingApproval === slot) pendingApproval = null
        }
        onStateChanged()
        return if (!signaled) null else slot.approved
    }

    internal fun saveConfirmedAndBurnInvite(
        inviteIdB64: String,
        peerFingerprintSha256: String,
        secretBytes: ByteArray,
        challengeBytes: ByteArray,
        signatureDer: ByteArray,
        save: () -> Unit
    ): Boolean {
        val now = clockMs()
        synchronized(lock) {
            val inv = activeInvite ?: return false
            if (inv.inviteIdB64 != inviteIdB64 || now >= inv.expiresAtMs) return false
            save()
            activeInvite = null
            failedAttempts = 0
            pendingApproval = null
            lastConfirmedReceipt = ConfirmedReceipt(
                inviteIdB64 = inviteIdB64,
                peerFingerprintSha256 = peerFingerprintSha256,
                secretBytes = secretBytes.copyOf(),
                challengeBytes = challengeBytes.copyOf(),
                signatureHex = SrapCrypto.toHexUpper(signatureDer),
                expiresAtMs = now + RECEIPT_RECOVERY_TTL_MS
            )
        }
        onStateChanged()
        return true
    }

    private fun cancelPendingLocked() {
        pendingApproval?.let {
            it.approved = false
            it.latch.countDown()
        }
        pendingApproval = null
    }

    companion object {
        const val DEFAULT_WINDOW_TTL_MS = 120_000L
        const val RECEIPT_RECOVERY_TTL_MS = 30_000L
        const val MAX_FAILED_ATTEMPTS = 5
    }
}

/**
 * Bridge connecting authenticated SRAP requests to Android local controllers (or JVM test fixtures).
 * Every write returns actual platform readback; route changes confirm asynchronously via onRouted.
 */
interface SrapOperationBridge : SrapMediaBridge, SrapClipboardBridge, SrapCaptureBridge, SrapDisplayBridge {
    val sharedFiles: SharedFileStore? get() = null
    fun currentPermissionSnapshot(): PermissionSnapshot
    fun listAudioOutputs(): List<AudioOutputSink>
    fun listAppCatalog(): AppCatalogSnapshot?
    fun readSystemMediaVolume(): SystemVolumeSnapshot
    fun writeSystemMediaVolume(sessionId: String, expectedRevision: Long, percent: Int): OperationResult<SystemVolumeSnapshot>
    fun writeSystemMediaVolumeIndex(sessionId: String, expectedRevision: Long, index: Int): OperationResult<SystemVolumeSnapshot> =
        OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
    fun startOwnedPlayback(sessionId: String, expectedRevision: Long, streamId: Long, targetKey: String): OperationResult<OwnedPlaybackSnapshot>
    fun startNetworkPlayback(sessionId: String, expectedRevision: Long, streamId: Long, targetKey: String,
        pcm: SrapPcmInput): OperationResult<OwnedPlaybackSnapshot> = OperationResult.Failure(ProtocolError.UNSUPPORTED)
    fun stopOwnedPlayback(sessionId: String, streamId: Long): Boolean
    fun readOwnedTrackVolume(sessionId: String, streamId: Long): OperationResult<Pair<Int, Boolean>>
    fun writeOwnedTrackVolume(
        sessionId: String,
        expectedRevision: Long,
        streamId: Long,
        gainPercent: Int?,
        muted: Boolean?
    ): OperationResult<Pair<Int, Boolean>>
    fun requestOwnedTrackRoute(
        sessionId: String,
        expectedRevision: Long,
        streamId: Long,
        requestId: String,
        targetKey: String,
        onRouted: (RoutedDeviceReadback) -> Unit
    ): OperationResult<Unit>
    fun cleanupSessionResources(sessionId: String)
}

/**
 * Single SRAP v1.0 server-side connection state machine.
 * Enforces framing, hello negotiation, mutual-TLS admission, time-bounded pairing,
 * capability revision & lease re-verification, strict 3-scope volume separation,
 * pending->audio.routed confirmation, idempotency within same session, and revocation cleanup.
 */
internal class SrapServerConnection(
    private val input: InputStream,
    private val output: OutputStream,
    private val closeTransport: () -> Unit,
    private val androidSpkiSha256Bytes: ByteArray,
    private val peerCertSpkiSha256Upper: String?,
    private val appVersion: String,
    private val peers: TrustedPeerRegistry,
    private val pairingWindow: PairingWindowController,
    private val bridge: SrapOperationBridge,
    private val eventExecutor: java.util.concurrent.Executor,
    private val receiverActive: () -> Boolean,
    private val aliases: SrapAliasBinding? = null,
    private val onEnding: () -> Unit = {},
    private val onPairingOnly: (Long) -> Unit = {},
    private val onAuthenticated: (sessionId: String, peer: TrustedPeer) -> Unit = { _, _ -> },
    private val onClosed: (sessionId: String?, peerFp: String?) -> Unit = { _, _ -> }
) : Closeable {

    private enum class Phase { AWAITING_HELLO, PAIRING_ONLY, AUTHENTICATED, CLOSED }

    private val sendLock = Any()
    private val writeDeadline = SrapWriteDeadline(closeTransport)
    private val stateLock = Any()
    private val capabilityGate = Any()
    private val notices = SrapPeerNotices(eventExecutor, ::pushCapabilities, ::sendMediaChanged, ::close)
    private val mediaRequests = SrapMediaRequests(bridge)
    private val capture = SrapCapture(bridge, eventExecutor, ::allocateEvenStream, {
        receiverActive() && sessionId != null && peerFingerprint?.let(peers::findByFingerprint) != null
    }, ::sendData) { id, error -> sendEvent(sessionId, "stream.canceled",
        mapOf("streamId" to StrictJson.num(id), "error" to StrictJson.str(error.wireId))) }
    private val fileRequests = SrapFiles({ bridge.sharedFiles }, bridge::currentPermissionSnapshot,
        { receiverActive() && synchronized(stateLock) { phase == Phase.AUTHENTICATED } &&
            peerFingerprint?.let(peers::findByFingerprint) != null }, ::claimIncomingData, ::releaseIncomingData, ::sendData)
    private val display = SrapDisplay(bridge, {
        receiverActive() && synchronized(stateLock) { phase == Phase.AUTHENTICATED } && peerFingerprint?.let(peers::findByFingerprint) != null
    }, ::allocateEvenStream, ::sendData)
    private var eventSeq = 0L
    private var phase = Phase.AWAITING_HELLO
    private var negotiatedMinor = 0
    @Volatile var sessionId: String? = null
        private set
    @Volatile var peerFingerprint: String? = peerCertSpkiSha256Upper
        private set
    private var trustedPeer: TrustedPeer? = null

    private var activeChallengeInviteId: ByteArray? = null
    private var activeChallengeBytes: ByteArray? = null
    private val ownedStreamLeases = LinkedHashMap<Long, OperationLease>()
    private val pcmInputs = HashMap<Long, SrapPcmInput>()
    private data class DataReceiver(val owner: Any, val receive: (ByteArray) -> Boolean)
    private val incomingData = HashMap<Long, DataReceiver>()
    private var lastEvenStream = 0L

    private fun allocateEvenStream(): Long = synchronized(stateLock) {
        check(phase == Phase.AUTHENTICATED && lastEvenStream < 0xfffffffeL)
        lastEvenStream += 2
        lastEvenStream
    }

    private fun claimIncomingData(id: Long, owner: Any, receive: (ByteArray) -> Boolean): Boolean = synchronized(stateLock) {
        if (phase != Phase.AUTHENTICATED || id !in 1L..0xffffffffL || id % 2L != 1L ||
            id in ownedStreamLeases || id in incomingData || incomingData.size >= 16) false
        else { incomingData[id] = DataReceiver(owner, receive); true }
    }

    private fun releaseIncomingData(id: Long, owner: Any) = synchronized(stateLock) {
        if (incomingData[id]?.owner === owner) incomingData.remove(id)
        Unit
    }

    private fun sendData(id: Long, bytes: ByteArray): Boolean = synchronized(sendLock) {
        if (!receiverActive() || peerFingerprint?.let(peers::findByFingerprint) == null ||
            synchronized(stateLock) { phase != Phase.AUTHENTICATED }) return@synchronized false
        writeDeadline.write { SrapCodec.writeFrame(output, SrapFrame(SrapFrameType.DATA, id, bytes)) }
    }
    private val pendingRequests = HashSet<String>()
    private data class Receipt(val params: JsonObject, val result: JsonObject)
    private val idempotencyCache = LinkedHashMap<String, Receipt>()
    private var lastIdempotencyGeneration = -1L
    private var lastSentCapabilityRevision = -1L
    private val clipboardRequests = SrapClipboardRequests(bridge, eventExecutor,
        trusted = { sid ->
            receiverActive() && synchronized(stateLock) { phase == Phase.AUTHENTICATED && sessionId == sid } &&
                peerFingerprint?.let(peers::findByFingerprint) != null
        },
        claim = ::claimIncomingData, release = ::releaseIncomingData, allocateEven = ::allocateEvenStream,
        respond = { sid, id, result ->
            val body = when (result) {
                is OperationResult.Done -> doneResponseBody(result.value)
                is OperationResult.Pending -> pendingResponseBody()
                is OperationResult.Failure -> failureToBody(result)
            }
            sendControl(SrapControlEnvelope("response", body, id = id, session = sid))
        },
        changed = { sid, watchId, revision, stream, bytes ->
            synchronized(sendLock) {
                val fields = linkedMapOf<String, JsonValue>("watchId" to StrictJson.str(watchId), "revision" to StrictJson.num(revision))
                if (bytes == null) sendEvent(sid, "clipboard.revision", fields)
                else {
                    fields["streamId"] = StrictJson.num(stream)
                    fields["byteLength"] = StrictJson.num(bytes.size)
                    if (sendEvent(sid, "clipboard.changed", fields) && bytes.isNotEmpty()) sendData(stream, bytes)
                }
            }
        })

    fun run() {
        try {
            while (true) {
                synchronized(stateLock) {
                    if (phase == Phase.CLOSED) return
                }
                val frame = try {
                    SrapCodec.readFrame(input)
                } catch (_: EOFException) {
                    return
                } catch (_: IOException) {
                    return
                } catch (e: SrapProtocolException) {
                    sendSessionError(ProtocolError.INVALID_ARGUMENT, e.message)
                    return
                } ?: return

                if (!receiverActive() || !handleFrame(frame)) return
            }
        } finally {
            close()
        }
    }

    fun queueCapabilities(snapshot: PermissionSnapshot) {
        if (sessionId != null) notices.capabilitiesChanged(snapshot)
    }

    fun mediaChanged() {
        if (sessionId != null && mediaRequests.isWatching()) notices.mediaChanged()
    }

    private fun sendMediaChanged() {
        val sid = sessionId ?: return
        if (mediaRequests.isWatching()) sendEvent(sid, "media.changed")
    }

    fun pushCapabilities(snapshot: PermissionSnapshot) { synchronized(capabilityGate) { publishCapabilities(snapshot) } }

    private fun publishCapabilities(snapshot: PermissionSnapshot) {
        capture.revalidate()
        display.revalidate()
        fileRequests.revalidate()
        clipboardRequests.update(snapshot)
        mediaRequests.update(snapshot)
        sessionId?.let { aliases?.revalidate(it) }
        val sid: String
        val cancelledStreams = ArrayList<Long>()
        synchronized(stateLock) {
            if (phase != Phase.AUTHENTICATED) return
            sid = sessionId ?: return
            if (snapshot.capabilityRevision <= lastSentCapabilityRevision) return
            lastSentCapabilityRevision = snapshot.capabilityRevision
            idempotencyCache.clear()
            lastIdempotencyGeneration = snapshot.generation
            val it = ownedStreamLeases.entries.iterator()
            while (it.hasNext()) {
                val (streamId, lease) = it.next()
                if (!lease.isValidFor(snapshot, requireExactCapabilityRevision = false)) {
                    cancelledStreams += streamId
                    pcmInputs.remove(streamId)?.close()
                    it.remove()
                }
            }
        }
        for (streamId in cancelledStreams) bridge.stopOwnedPlayback(sid, streamId)
        sendControl(SrapControlEnvelope(type = "capabilities", session = sid, body = capabilitiesBody(snapshot)))
        for (streamId in cancelledStreams) {
            sendEvent(
                session = sid,
                name = "stream.canceled",
                extra = mapOf(
                    "streamId" to StrictJson.num(streamId),
                    "error" to StrictJson.str(ProtocolError.PERMISSION_LOST.wireId),
                    "reason" to StrictJson.str("lease_invalidated")
                )
            )
        }
    }

    fun endSession(error: ProtocolError, reason: String) {
        sessionId?.let { aliases?.cancel(it, reason = error) }
        try {
            if (sessionId != null) {
                onEnding()
                sendSessionError(error, reason, session = sessionId)
            }
        } finally { close() }
    }

    private fun handleFrame(frame: SrapFrame): Boolean {
        val currentPhase = synchronized(stateLock) { phase }
        if (frame.type == SrapFrameType.DATA) {
            if (currentPhase != Phase.AUTHENTICATED) {
                sendSessionError(ProtocolError.NOT_PAIRED, "data_before_auth")
                return false
            }
            val claimed = synchronized(stateLock) { incomingData[frame.streamId] }
            if (claimed != null) {
                if (peerFingerprint?.let(peers::findByFingerprint) == null || !claimed.receive(frame.payload)) {
                    sendSessionError(ProtocolError.INVALID_ARGUMENT, "unexpected_stream_data", session = sessionId)
                    return false
                }
                return true
            }
            val current = bridge.currentPermissionSnapshot()
            val authorized = peerFingerprint?.let(peers::findByFingerprint) != null
            val accepted = synchronized(stateLock) {
                authorized && phase == Phase.AUTHENTICATED &&
                    ownedStreamLeases[frame.streamId]?.isValidFor(current, requireExactCapabilityRevision = false) == true &&
                    pcmInputs[frame.streamId]?.receive(frame.payload) == true
            }
            if (!accepted) {
                sendSessionError(ProtocolError.INVALID_ARGUMENT, "invalid_data_stream", session = sessionId)
                return false
            }
            return true
        }

        val env = try {
            SrapCodec.parseControl(frame)
        } catch (e: SrapProtocolException) {
            sendSessionError(ProtocolError.INVALID_ARGUMENT, e.message, session = sessionId)
            return false
        }

        return when (currentPhase) {
            Phase.AWAITING_HELLO -> handleHello(env)
            Phase.PAIRING_ONLY -> handlePairingMessage(env)
            Phase.AUTHENTICATED -> handleAuthenticatedMessage(env)
            Phase.CLOSED -> false
        }
    }

    private fun handleHello(env: SrapControlEnvelope): Boolean {
        if (env.type != "hello" || env.session != null) {
            sendSessionError(ProtocolError.INVALID_ARGUMENT, "expected_hello_first")
            return false
        }
        val peerHello = try {
            SrapCodec.parseHello(env.body)
        } catch (e: SrapProtocolException) {
            sendSessionError(ProtocolError.INVALID_ARGUMENT, e.message)
            return false
        }
        val minor = SrapCodec.negotiateMinor(peerHello)
        if (minor == null) {
            sendEvent(
                session = null,
                name = "session.error",
                extra = mapOf("error" to StrictJson.str(ProtocolError.VERSION_MISMATCH.wireId))
            )
            return false
        }
        negotiatedMinor = minor
        sendControl(
            SrapControlEnvelope(
                type = "hello",
                id = env.id,
                body = SrapCodec.encodeHello(appVersion = appVersion)
            )
        )

        val peer = peerCertSpkiSha256Upper?.let { peers.findByFingerprint(it) }
        if (peer != null) {
            val sid = SrapCrypto.randomSessionId()
            synchronized(capabilityGate) {
                synchronized(stateLock) {
                    phase = Phase.AUTHENTICATED
                    sessionId = sid
                    trustedPeer = peer
                    peerFingerprint = peer.fingerprintSha256
                }
                onAuthenticated(sid, peer)
                val snapshot = bridge.currentPermissionSnapshot()
                synchronized(stateLock) {
                    lastIdempotencyGeneration = snapshot.generation
                    lastSentCapabilityRevision = snapshot.capabilityRevision
                }
                sendControl(SrapControlEnvelope(type = "capabilities", session = sid, body = capabilitiesBody(snapshot)))
            }
            runCatching { peers.touchLastConnected(peer.fingerprintSha256, System.currentTimeMillis()) }
            return true
        }

        if (pairingWindow.isWindowOpen() || pairingWindow.hasActiveReceiptRecovery()) {
            synchronized(stateLock) { phase = Phase.PAIRING_ONLY }
            val remaining = pairingWindow.currentInvite()?.let { it.expiresAtMs - System.currentTimeMillis() }
                ?: PairingWindowController.RECEIPT_RECOVERY_TTL_MS
            onPairingOnly(remaining.coerceIn(1L, PairingWindowController.DEFAULT_WINDOW_TTL_MS))
            return true
        }

        sendSessionError(ProtocolError.NOT_PAIRED, "pairing_window_closed")
        return false
    }

    private fun handlePairingMessage(env: SrapControlEnvelope): Boolean {
        if (env.session != null) {
            sendSessionError(ProtocolError.INVALID_ARGUMENT, "unexpected_session_during_pairing")
            return false
        }
        val reqId = env.id ?: run {
            sendSessionError(ProtocolError.INVALID_ARGUMENT, "missing_pairing_request_id")
            return false
        }
        return when (env.type) {
            "pair.begin" -> handlePairBegin(reqId, env.body)
            "pair.prove" -> handlePairProve(reqId, env.body)
            else -> {
                sendSessionError(ProtocolError.NOT_PAIRED, "disallowed_message_during_pairing:${env.type}")
                false
            }
        }
    }

    private fun handlePairBegin(reqId: String, body: JsonObject): Boolean {
        val inviteIdB64 = try {
            SrapCodec.requiredText(body, "inviteId", 64)
        } catch (e: SrapProtocolException) {
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return false
        }
        val inviteIdBytes = try {
            SrapCrypto.base64UrlDecodeNoPad(inviteIdB64)
        } catch (e: SrapProtocolException) {
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return false
        }
        if (inviteIdBytes.size != 16) {
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, "invalid_invite_id_size")
            return false
        }
        val challenge = pairingWindow.issueChallengeForBegin(inviteIdBytes)
        if (challenge == null) {
            pairingWindow.recordFailureAndCheckBurned()
            sendPairError(reqId, ProtocolError.NOT_PAIRED, "invite_expired_or_unknown")
            return false
        }
        activeChallengeInviteId = inviteIdBytes
        activeChallengeBytes = challenge
        sendControl(
            SrapControlEnvelope(
                type = "pair.challenge",
                id = reqId,
                body = StrictJson.obj(
                    "inviteId" to StrictJson.str(inviteIdB64),
                    "challenge" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(challenge))
                )
            )
        )
        return true
    }

    private fun handlePairProve(reqId: String, body: JsonObject): Boolean {
        val expectedInviteId = activeChallengeInviteId
        val challenge = activeChallengeBytes
        if (expectedInviteId == null || challenge == null) {
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, "missing_pair_begin")
            return false
        }
        activeChallengeInviteId = null
        activeChallengeBytes = null

        val inviteIdB64: String
        val secretB64: String
        val pubKeyB64: String
        val sigB64: String
        val displayName: String
        try {
            inviteIdB64 = SrapCodec.requiredText(body, "inviteId", 64)
            secretB64 = SrapCodec.requiredText(body, "secret", 128)
            pubKeyB64 = SrapCodec.requiredText(body, "publicKey", 512)
            sigB64 = SrapCodec.requiredText(body, "signature", 512)
            displayName = SrapCodec.requiredText(body, "displayName", 64).trim()
            if (displayName.isEmpty()) throw SrapProtocolException("Empty displayName")
        } catch (e: SrapProtocolException) {
            pairingWindow.recordFailureAndCheckBurned()
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return false
        }

        val inviteIdBytes: ByteArray
        val secretBytes: ByteArray
        val pubKeyDer: ByteArray
        val sigDer: ByteArray
        try {
            inviteIdBytes = SrapCrypto.base64UrlDecodeNoPad(inviteIdB64)
            secretBytes = SrapCrypto.base64UrlDecodeNoPad(secretB64)
            pubKeyDer = SrapCrypto.base64UrlDecodeNoPad(pubKeyB64)
            sigDer = SrapCrypto.base64UrlDecodeNoPad(sigB64)
            SrapCrypto.parseEcP256Spki(pubKeyDer)
        } catch (e: SrapProtocolException) {
            pairingWindow.recordFailureAndCheckBurned()
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return false
        }

        if (!SrapCrypto.constantTimeEquals(inviteIdBytes, expectedInviteId)) {
            pairingWindow.recordFailureAndCheckBurned()
            sendPairError(reqId, ProtocolError.INVALID_ARGUMENT, "invite_id_mismatch")
            return false
        }

        val windowsSpkiSha256 = SrapCrypto.sha256(pubKeyDer)
        val windowsFpUpper = SrapCrypto.toHexUpper(windowsSpkiSha256)

        // If the client presented a TLS certificate, its SPKI must match pair.prove.publicKey.
        if (peerCertSpkiSha256Upper != null && peerCertSpkiSha256Upper != windowsFpUpper) {
            pairingWindow.recordFailureAndCheckBurned()
            sendPairError(reqId, ProtocolError.IDENTITY_CONFLICT, "tls_cert_key_mismatch")
            return false
        }

        // Receipt recovery path: allow recovering lost ACK without re-consuming an invite.
        if (pairingWindow.checkReceiptRecovery(inviteIdB64, windowsFpUpper, secretBytes, sigDer)) {
            sendControl(
                SrapControlEnvelope(
                    type = "pair.result",
                    id = reqId,
                    body = StrictJson.obj("status" to StrictJson.str("done"))
                )
            )
            return false
        }

        val activeInvite = pairingWindow.currentInvite()
        if (activeInvite == null || !SrapCrypto.constantTimeEquals(activeInvite.inviteId, inviteIdBytes)) {
            pairingWindow.recordFailureAndCheckBurned()
            sendPairError(reqId, ProtocolError.TIMEOUT, "invite_expired")
            return false
        }

        if (!SrapCrypto.constantTimeEquals(activeInvite.secret, secretBytes)) {
            val burned = pairingWindow.recordFailureAndCheckBurned()
            sendPairError(
                reqId,
                ProtocolError.PERMISSION_REQUIRED,
                if (burned) "invite_locked_max_attempts" else "invalid_secret"
            )
            return false
        }

        val transcript = SrapCrypto.buildPairingTranscript(
            inviteId = inviteIdBytes,
            challenge = challenge,
            androidSpkiSha256 = androidSpkiSha256Bytes,
            windowsSpkiSha256 = windowsSpkiSha256,
            major = SrapCodec.MAJOR,
            minor = negotiatedMinor
        )
        if (!SrapCrypto.verifyEcdsaP256Sha256(pubKeyDer, transcript, sigDer)) {
            val burned = pairingWindow.recordFailureAndCheckBurned()
            sendPairError(
                reqId,
                ProtocolError.PERMISSION_REQUIRED,
                if (burned) "invite_locked_max_attempts" else "invalid_signature"
            )
            return false
        }

        // Wait for explicit local Android confirmation before persisting trust.
        val pendingReq = PendingPairingRequest(
            inviteIdB64 = inviteIdB64,
            peerFingerprintSha256 = windowsFpUpper,
            displayName = displayName,
            publicKeyDerB64 = pubKeyB64
        )
        val remainingMs = (activeInvite.expiresAtMs - System.currentTimeMillis()).coerceAtLeast(1_000L)
        val decision = pairingWindow.awaitLocalConfirmation(pendingReq, remainingMs) {
            sendControl(
                SrapControlEnvelope(
                    type = "pair.result",
                    id = reqId,
                    body = StrictJson.obj("status" to StrictJson.str("pending"))
                )
            )
        }
        when (decision) {
            true -> {
                val now = System.currentTimeMillis()
                val confirmed = try {
                    pairingWindow.saveConfirmedAndBurnInvite(inviteIdB64, windowsFpUpper, secretBytes, challenge, sigDer) {
                        peers.savePeer(TrustedPeer(
                        fingerprintSha256 = windowsFpUpper,
                        displayName = displayName,
                        publicKeyDerB64 = pubKeyB64,
                        pairedAtMs = now,
                        lastConnectedAtMs = now
                        ))
                    }
                } catch (_: Exception) {
                    sendPairError(reqId, ProtocolError.IO_FAILURE, "trust_store_write_failed")
                    return false
                }
                if (!confirmed) {
                    sendPairError(reqId, ProtocolError.CANCELED, "pairing_window_closed")
                    return false
                }
                sendControl(
                    SrapControlEnvelope(
                        type = "pair.result",
                        id = reqId,
                        body = StrictJson.obj("status" to StrictJson.str("done"))
                    )
                )
            }
            false -> sendPairError(reqId, ProtocolError.CANCELED, "user_rejected_pairing")
            null -> sendPairError(reqId, ProtocolError.TIMEOUT, "confirmation_timeout_or_busy")
        }
        // Operational sessions always establish over Mutual TLS after pairing completes.
        return false
    }

    private fun handleAuthenticatedMessage(env: SrapControlEnvelope): Boolean {
        val sid = sessionId
        if (sid == null || env.session != sid) {
            sendSessionError(ProtocolError.STALE_SESSION, "session_mismatch", session = sid)
            return false
        }
        // Re-verify peer has not been revoked.
        val fp = peerFingerprint
        if (fp == null || peers.findByFingerprint(fp) == null) {
            sendSessionError(ProtocolError.PERMISSION_LOST, "peer_revoked", session = sid)
            return false
        }

        return when (env.type) {
            "request" -> {
                val reqId = env.id
                if (reqId == null) {
                    sendSessionError(ProtocolError.INVALID_ARGUMENT, "missing_request_id", session = sid)
                    return false
                }
                dispatchRequest(sid, reqId, env.body)
                true
            }
            "cancel" -> {
                handleCancel(sid, env.id, env.body)
                true
            }
            else -> {
                sendSessionError(ProtocolError.INVALID_ARGUMENT, "unexpected_message_type:${env.type}", session = sid)
                false
            }
        }
    }

    private fun dispatchRequest(sid: String, reqId: String, body: JsonObject) {
        val op: String
        val expectedCapRev: Long
        val params: JsonObject
        val idempotencyKey: String?
        try {
            op = SrapCodec.requiredText(body, "op")
            expectedCapRev = SrapCodec.requiredLong(body, "capabilityRevision")
            if (expectedCapRev < 0L) throw SrapProtocolException("Negative capabilityRevision")
            params = SrapCodec.requiredObject(body, "params")
            idempotencyKey = SrapCodec.optionalText(body, "idempotencyKey")
        } catch (e: SrapProtocolException) {
            sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return
        }

        val snapshot = bridge.currentPermissionSnapshot()
        if (expectedCapRev != snapshot.capabilityRevision) {
            pushCapabilities(snapshot)
            sendResponseError(sid, reqId, ProtocolError.STALE_SESSION, "capability_revision_mismatch")
            return
        }
        if (idempotencyKey != null && (idempotencyKey.length !in 1..128 || idempotencyKey.any { it.code !in 33..126 })) {
            sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "invalid_idempotency_key")
            return
        }
        if (op in CLIPBOARD_OPERATIONS) {
            if (idempotencyKey != null) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "clipboard_replay_not_supported")
                return
            }
            clipboardRequests.request(sid, reqId, op, params, expectedCapRev)
            return
        }
        if (op in setOf("file.list", "file.offer", "file.open", "file.chunk", "file.finish")) {
            val peer = trustedPeer
            if (peer == null || idempotencyKey != null) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT)
                return
            }
            fileRequests.execute(peer, reqId, op, expectedCapRev, params) { result ->
                val response = when (result) {
                    is OperationResult.Done -> doneResponseBody(result.value)
                    is OperationResult.Failure -> failureToBody(result)
                    is OperationResult.Pending -> pendingResponseBody()
                }
                sendControl(SrapControlEnvelope("response", response, id = reqId, session = sid))
            }
            return
        }
        if (op == "display.start" || op == "display.next") {
            if (idempotencyKey != null) { sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT); return }
            fun reply(result: OperationResult<JsonObject>) {
                val body = when (result) {
                    is OperationResult.Done -> doneResponseBody(result.value)
                    is OperationResult.Failure -> failureToBody(result)
                    is OperationResult.Pending -> errorResponseBody(ProtocolError.IO_FAILURE)
                }
                sendControl(SrapControlEnvelope("response", body, id = reqId, session = sid))
            }
            try {
                if (op == "display.next") display.next(reqId, params, ::reply)
                else reply(display.open(sid, peerFingerprint.orEmpty(), expectedCapRev, params, reqId))
            } catch (_: SrapProtocolException) { sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT) }
            return
        }
        if (op == "alias.bind.begin") {
            if (idempotencyKey != null) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "alias_replay_not_supported")
                return
            }
            beginAlias(sid, reqId, params, snapshot, expectedCapRev)
            return
        }
        if (op == "media.get" || op == "media.control" || op == "media.unwatch") {
            if (idempotencyKey != null) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "media_replay_not_supported")
                return
            }
            try {
                mediaRequests.execute(sid, reqId, op, expectedCapRev, params) { result ->
                    val response = when (result) {
                        is OperationResult.Done -> doneResponseBody(result.value)
                        is OperationResult.Failure -> failureToBody(result)
                        is OperationResult.Pending -> pendingResponseBody()
                    }
                    sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = response))
                }
            } catch (_: SrapProtocolException) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT)
            }
            return
        }
        val receiptKey = if (idempotencyKey != null && (op == "volume.set" || op == "audio.play.start" || op == "stream.stop"))
            "$op:$idempotencyKey" else null
        if (receiptKey != null) {
            val cached = synchronized(stateLock) {
                if (lastIdempotencyGeneration == snapshot.generation && lastSentCapabilityRevision == expectedCapRev)
                    idempotencyCache[receiptKey] else null
            }
            if (cached != null) {
                if (cached.params != params) sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "idempotency_key_conflict")
                else if (op == "audio.play.start" && synchronized(stateLock) {
                    (params["streamId"] as? JsonNumber)?.toLongExactOrNull() !in ownedStreamLeases.keys
                }) sendResponseError(sid, reqId, ProtocolError.STALE_SESSION, "stream_retired")
                else sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = cached.result))
                return
            }
        }

        val responseBody: JsonObject? = when (op) {
            "audio.devices.list" -> handleAudioDevicesList(snapshot, expectedCapRev)
            "catalog.list" -> handleCatalogList(snapshot, expectedCapRev)
            "volume.get" -> handleVolumeGet(sid, snapshot, expectedCapRev, params)
            "volume.set" -> handleVolumeSet(sid, snapshot, expectedCapRev, params)
            "audio.play.start" -> handleAudioPlayStart(sid, snapshot, expectedCapRev, params)
            "stream.stop" -> handleStreamStop(sid, snapshot, expectedCapRev, params)
            "audio.route.set" -> {
                handleAudioRouteSet(sid, reqId, snapshot, expectedCapRev, params)
                null // Sends its own pending/error response
            }
            "audio.capture.start", "audio.capture.credit" -> try {
                if (idempotencyKey != null) errorResponseBody(ProtocolError.INVALID_ARGUMENT, "capture_replay_not_supported")
                else {
                    val captureResult = if (op == "audio.capture.credit") capture.credit(params)
                        else capture.open(sid, peerFingerprint.orEmpty(), expectedCapRev, params)
                    when (captureResult) {
                        is OperationResult.Done -> doneResponseBody(captureResult.value)
                        is OperationResult.Failure -> failureToBody(captureResult)
                        is OperationResult.Pending -> errorResponseBody(ProtocolError.IO_FAILURE)
                    }
                }
            } catch (_: SrapProtocolException) { errorResponseBody(ProtocolError.INVALID_ARGUMENT) }
            "display.control" ->
                errorResponseBody(ProtocolError.UNSUPPORTED, "no_uid_scoped_input_display")
            else ->
                errorResponseBody(ProtocolError.INVALID_ARGUMENT, "unsupported_operation")
        }

        if (responseBody != null) {
            if (receiptKey != null && (responseBody["status"] as? JsonString)?.value == "done") {
                synchronized(stateLock) {
                    if (lastIdempotencyGeneration == snapshot.generation && lastSentCapabilityRevision == expectedCapRev) {
                        if (idempotencyCache.size >= 64) {
                            idempotencyCache.remove(idempotencyCache.keys.first())
                        }
                        idempotencyCache[receiptKey] = Receipt(params, responseBody)
                    }
                }
            }
            sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = responseBody))
        }
    }

    private fun beginAlias(sid: String, reqId: String, params: JsonObject, snapshot: PermissionSnapshot, expectedRev: Long) {
        val controller = aliases
        val peer = trustedPeer
        if (controller == null || peer == null) {
            sendResponseError(sid, reqId, ProtocolError.NOT_IMPLEMENTED)
            return
        }
        val result = snapshot.acquireLease(Capability.ADB_ALIAS, "paired_profile", expectedRev)
        if (result !is OperationResult.Done) {
            sendResponseError(sid, reqId, (result as OperationResult.Failure).error)
            return
        }
        val lease = result.value
        fun checkAccess(): ProtocolError? {
            val live = synchronized(stateLock) { phase == Phase.AUTHENTICATED && sessionId == sid }
            if (!live || !receiverActive()) return ProtocolError.STALE_SESSION
            if (peers.findByFingerprint(peer.fingerprintSha256) == null) return ProtocolError.PERMISSION_LOST
            val current = bridge.currentPermissionSnapshot()
            if (current.entry(Capability.ADB_ALIAS).status != CapabilityStatus.AVAILABLE) return ProtocolError.PERMISSION_LOST
            return if (lease.isValidFor(current)) null else ProtocolError.STALE_SESSION
        }
        val parent = SrapAliasBinding.Parent(sid, reqId, negotiatedMinor, peer, ::checkAccess,
            respond = { response ->
                val body = when (response) {
                    is OperationResult.Done -> doneResponseBody(response.value)
                    is OperationResult.Failure -> failureToBody(response)
                    is OperationResult.Pending -> errorResponseBody(ProtocolError.IO_FAILURE)
                }
                sendControl(SrapControlEnvelope("response", body, id = reqId, session = sid))
            },
            sendProof = { fields ->
                synchronized(sendLock) {
                    checkAccess() == null && sendEvent(sid, "alias.bind.proof", fields)
                }
            })
        controller.begin(parent, params)?.let { sendResponseError(sid, reqId, it) }
    }

    private fun capabilitiesBody(snapshot: PermissionSnapshot): JsonObject {
        var entries = snapshot.capabilityEntries
        if (aliases == null && snapshot.entry(Capability.ADB_ALIAS).status == CapabilityStatus.AVAILABLE) {
            entries = entries + (Capability.ADB_ALIAS to CapabilityEntry(CapabilityStatus.NOT_IMPLEMENTED))
        }
        if (snapshot.entry(Capability.CAPTURE_APP).status == CapabilityStatus.AVAILABLE && !bridge.grantsCapture(sessionId.orEmpty(), peerFingerprint.orEmpty())) {
            entries = entries + (Capability.CAPTURE_APP to CapabilityEntry(CapabilityStatus.NEEDS_AUTHORIZATION, reason = "local_playback_capture_consent"))
        }
        if (snapshot.entry(Capability.MIRROR_DISPLAY).status == CapabilityStatus.AVAILABLE &&
            !bridge.grantsDisplay(sessionId.orEmpty(), peerFingerprint.orEmpty())) {
            entries = entries + (Capability.MIRROR_DISPLAY to CapabilityEntry(CapabilityStatus.NEEDS_AUTHORIZATION,
                reason = "local_screen_consent", scope = "one_projection"))
        }
        return SrapCodec.encodeCapabilitiesBody(snapshot.copy(capabilityEntries = entries))
    }

    private fun handleAudioDevicesList(snapshot: PermissionSnapshot, expectedRev: Long): JsonObject {
        val lease = snapshot.acquireLease(Capability.AUDIO_DEVICES, "local_outputs", expectedRev)
        if (lease is OperationResult.Failure) return failureToBody(lease)
        val sinks = bridge.listAudioOutputs().map { s ->
            StrictJson.obj(
                "key" to StrictJson.str(s.key),
                "kind" to StrictJson.str(s.kind.wireId),
                "id" to StrictJson.num(s.androidDeviceId),
                "type" to StrictJson.num(s.androidDeviceType),
                "name" to StrictJson.str(s.name),
                "address" to s.address?.let { StrictJson.str(it) },
                "isDefault" to StrictJson.bool(s.isDefault),
                "isFallbackProfile" to StrictJson.bool(s.isFallbackProfile)
            )
        }
        return doneResponseBody(StrictJson.obj("outputs" to StrictJson.arr(sinks)))
    }

    private fun handleCatalogList(snapshot: PermissionSnapshot, expectedRev: Long): JsonObject {
        val capScope = snapshot.entry(Capability.APP_CATALOG).scope ?: "launcher_intents"
        val lease = snapshot.acquireLease(Capability.APP_CATALOG, capScope, expectedRev)
        if (lease is OperationResult.Failure) return failureToBody(lease)
        val catalog = bridge.listAppCatalog() ?: return errorResponseBody(ProtocolError.BUSY, "catalog_loading")
        val apps = catalog.entries.map { e ->
            // External clients address apps by stable appKey (package name), never raw UID.
            StrictJson.obj(
                "appKey" to StrictJson.str(e.packageName),
                "label" to StrictJson.str(e.label),
                "isSelf" to StrictJson.bool(e.isSelf)
            )
        }
        return doneResponseBody(
            StrictJson.obj(
                "visibilityScope" to StrictJson.str(catalog.visibilityScope),
                "apps" to StrictJson.arr(apps)
            )
        )
    }

    private fun handleVolumeGet(
        sid: String,
        snapshot: PermissionSnapshot,
        expectedRev: Long,
        params: JsonObject
    ): JsonObject {
        val scope = try {
            SrapCodec.requiredText(params, "scope")
        } catch (e: SrapProtocolException) {
            return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
        }
        return when (scope) {
            "system_media" -> {
                val lease = snapshot.acquireLease(Capability.PHONE_VOLUME, "system_media", expectedRev)
                if (lease is OperationResult.Failure) return failureToBody(lease)
                val v = bridge.readSystemMediaVolume()
                doneResponseBody(volumeSystemJson(v))
            }
            "owned_track" -> {
                val lease = snapshot.acquireLease(Capability.PLAY_AUDIO, "owned_track", expectedRev)
                if (lease is OperationResult.Failure) return failureToBody(lease)
                val streamId = try {
                    SrapCodec.requiredLong(params, "streamId")
                } catch (e: SrapProtocolException) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
                }
                if (streamId <= 0L) return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "invalid_stream_id")
                when (val r = bridge.readOwnedTrackVolume(sid, streamId)) {
                    is OperationResult.Done -> doneResponseBody(
                        StrictJson.obj(
                            "scope" to StrictJson.str("owned_track"),
                            "streamId" to StrictJson.num(streamId),
                            "trackGainPercent" to StrictJson.num(r.value.first),
                            "trackMuted" to StrictJson.bool(r.value.second)
                        )
                    )
                    is OperationResult.Failure -> failureToBody(r)
                    is OperationResult.Pending -> pendingResponseBody()
                }
            }
            "other_app" -> {
                if (params.has("uid")) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "external_uid_forbidden")
                }
                val appKey = try {
                    SrapCodec.requiredText(params, "appKey")
                } catch (e: SrapProtocolException) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
                }
                val lease = snapshot.acquireLease(Capability.VOLUME_OTHER_APPS, "other_app:$appKey", expectedRev)
                if (lease is OperationResult.Failure) return failureToBody(lease)
                errorResponseBody(ProtocolError.NOT_IMPLEMENTED, "stage4_other_app_volume_pending")
            }
            else -> errorResponseBody(ProtocolError.INVALID_ARGUMENT, "unknown_volume_scope")
        }
    }

    private fun handleVolumeSet(
        sid: String,
        snapshot: PermissionSnapshot,
        expectedRev: Long,
        params: JsonObject
    ): JsonObject {
        val scope = try {
            SrapCodec.requiredText(params, "scope")
        } catch (e: SrapProtocolException) {
            return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
        }
        return when (scope) {
            "system_media" -> {
                val lease = snapshot.acquireLease(Capability.PHONE_VOLUME, "system_media", expectedRev)
                if (lease is OperationResult.Failure) return failureToBody(lease)
                val indexed = params.has("streamIndex")
                if (indexed == params.has("percent") || params.fields.size != 2)
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "one_volume_value_required")
                if (indexed && snapshot.entry(Capability.PHONE_VOLUME).limits["indexWrite"] != "1")
                    return errorResponseBody(ProtocolError.UNSUPPORTED, "index_write_unavailable")
                val value = try {
                    SrapCodec.requiredInt(params, if (indexed) "streamIndex" else "percent")
                } catch (e: SrapProtocolException) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
                }
                if (!indexed && value !in 0..100) return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "percent_out_of_range")
                when (val r = if (indexed) bridge.writeSystemMediaVolumeIndex(sid, expectedRev, value)
                    else bridge.writeSystemMediaVolume(sid, expectedRev, value)) {
                    is OperationResult.Done -> doneResponseBody(volumeSystemJson(r.value))
                    is OperationResult.Failure -> failureToBody(r)
                    is OperationResult.Pending -> pendingResponseBody()
                }
            }
            "owned_track" -> {
                val lease = snapshot.acquireLease(Capability.PLAY_AUDIO, "owned_track", expectedRev)
                if (lease is OperationResult.Failure) return failureToBody(lease)
                val streamId: Long
                val gain: Int?
                val muted: Boolean?
                try {
                    streamId = SrapCodec.requiredLong(params, "streamId")
                    gain = if (params.has("trackGainPercent")) SrapCodec.requiredInt(params, "trackGainPercent") else null
                    muted = SrapCodec.optionalBool(params, "trackMuted")
                } catch (e: SrapProtocolException) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
                }
                if (streamId <= 0L || (gain == null && muted == null) || (gain != null && gain !in 0..100)) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "invalid_owned_track_volume_params")
                }
                when (val r = bridge.writeOwnedTrackVolume(sid, expectedRev, streamId, gain, muted)) {
                    is OperationResult.Done -> doneResponseBody(
                        StrictJson.obj(
                            "scope" to StrictJson.str("owned_track"),
                            "streamId" to StrictJson.num(streamId),
                            "trackGainPercent" to StrictJson.num(r.value.first),
                            "trackMuted" to StrictJson.bool(r.value.second)
                        )
                    )
                    is OperationResult.Failure -> failureToBody(r)
                    is OperationResult.Pending -> pendingResponseBody()
                }
            }
            "other_app" -> {
                if (params.has("uid")) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "external_uid_forbidden")
                }
                val appKey: String
                val percent: Int
                try {
                    appKey = SrapCodec.requiredText(params, "appKey")
                    percent = SrapCodec.requiredInt(params, "percent")
                } catch (e: SrapProtocolException) {
                    return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
                }
                if (percent !in 0..100) return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "percent_out_of_range")
                val lease = snapshot.acquireLease(Capability.VOLUME_OTHER_APPS, "other_app:$appKey", expectedRev)
                if (lease is OperationResult.Failure) return failureToBody(lease)
                errorResponseBody(ProtocolError.NOT_IMPLEMENTED, "stage4_other_app_volume_pending")
            }
            else -> errorResponseBody(ProtocolError.INVALID_ARGUMENT, "unknown_volume_scope")
        }
    }

    private fun handleAudioPlayStart(
        sid: String,
        snapshot: PermissionSnapshot,
        expectedRev: Long,
        params: JsonObject
    ): JsonObject {
        val leaseRes = snapshot.acquireLease(Capability.PLAY_AUDIO, "owned_track", expectedRev)
        if (leaseRes is OperationResult.Failure) return failureToBody(leaseRes)
        val lease = (leaseRes as OperationResult.Done).value
        val streamId: Long
        val targetKey: String
        val encoding: String?
        try {
            streamId = SrapCodec.requiredLong(params, "streamId")
            targetKey = SrapCodec.optionalText(params, "targetKey") ?: AudioOutputSink.KEY_CURRENT
            encoding = SrapCodec.optionalText(params, "encoding")
        } catch (e: SrapProtocolException) {
            return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
        }
        // Per docs/protocol.md, Windows allocates odd stream IDs (1, 3, 5...).
        if (streamId <= 0L || streamId > 0xFFFFFFFFL || (streamId % 2L) == 0L) {
            return errorResponseBody(ProtocolError.INVALID_ARGUMENT, "client_stream_id_must_be_odd")
        }
        if (encoding != null && (encoding != SrapPcmInput.FORMAT || lease.authorizedLimits["pcm"] != encoding)) {
            return errorResponseBody(ProtocolError.UNSUPPORTED, "pcm_format_not_negotiated")
        }
        if (synchronized(stateLock) { streamId in ownedStreamLeases || streamId in incomingData }) {
            return errorResponseBody(ProtocolError.BUSY, "stream_id_in_use")
        }
        val pcm = encoding?.let { SrapPcmInput(eventExecutor) { next ->
            sendEvent(sid, "audio.credit", mapOf("streamId" to StrictJson.num(streamId), "nextSeq" to StrictJson.num(next)))
        } }
        val start = if (pcm == null) bridge.startOwnedPlayback(sid, expectedRev, streamId, targetKey)
            else bridge.startNetworkPlayback(sid, expectedRev, streamId, targetKey, pcm)
        return when (val r = start) {
            is OperationResult.Done -> {
                val admitted = synchronized(stateLock) {
                    if (phase != Phase.AUTHENTICATED || !lease.isValidFor(bridge.currentPermissionSnapshot(), false)) false
                    else {
                        ownedStreamLeases[streamId] = lease
                        if (pcm != null) pcmInputs[streamId] = pcm
                        true
                    }
                }
                if (!admitted) {
                    pcm?.close()
                    bridge.stopOwnedPlayback(sid, streamId)
                    return errorResponseBody(ProtocolError.STALE_SESSION, "playback_lease_changed")
                }
                doneResponseBody(
                    StrictJson.obj(
                        "streamId" to StrictJson.num(streamId),
                        "encoding" to encoding?.let(StrictJson::str),
                        "credit" to pcm?.let { StrictJson.num(SrapPcmInput.WINDOW) },
                        "targetKey" to StrictJson.str(r.value.targetKey),
                        "trackGainPercent" to StrictJson.num(r.value.trackGainPercent),
                        "trackMuted" to StrictJson.bool(r.value.trackMuted)
                    )
                )
            }
            is OperationResult.Failure -> failureToBody(r)
            is OperationResult.Pending -> pendingResponseBody()
        }
    }

    private fun handleStreamStop(
        sid: String,
        snapshot: PermissionSnapshot,
        expectedRev: Long,
        params: JsonObject
    ): JsonObject {
        if (expectedRev != snapshot.capabilityRevision) {
            return errorResponseBody(ProtocolError.STALE_SESSION, "capability_revision_mismatch", retryable = true)
        }
        val streamId = try {
            SrapCodec.requiredLong(params, "streamId")
        } catch (e: SrapProtocolException) {
            return errorResponseBody(ProtocolError.INVALID_ARGUMENT, e.message)
        }
        synchronized(stateLock) { ownedStreamLeases.remove(streamId); pcmInputs.remove(streamId)?.close() }
        val stopped = display.stop(streamId) || capture.stop(streamId) || bridge.stopOwnedPlayback(sid, streamId)
        return doneResponseBody(
            StrictJson.obj(
                "streamId" to StrictJson.num(streamId),
                "stopped" to StrictJson.bool(stopped)
            )
        )
    }

    private fun handleAudioRouteSet(
        sid: String,
        reqId: String,
        snapshot: PermissionSnapshot,
        expectedRev: Long,
        params: JsonObject
    ) {
        val scope: String
        val targetKey: String
        try {
            scope = SrapCodec.optionalText(params, "scope") ?: "owned_track"
            targetKey = SrapCodec.requiredText(params, "targetKey")
        } catch (e: SrapProtocolException) {
            sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return
        }

        if (scope == "other_app") {
            if (params.has("uid")) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "external_uid_forbidden")
                return
            }
            val appKey = try {
                SrapCodec.requiredText(params, "appKey")
            } catch (e: SrapProtocolException) {
                sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, e.message)
                return
            }
            val leaseRes = snapshot.acquireLease(Capability.ROUTE_OTHER_APPS, "other_app:$appKey", expectedRev)
            if (leaseRes is OperationResult.Failure) {
                sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = failureToBody(leaseRes)))
                return
            }
            sendResponseError(sid, reqId, ProtocolError.NOT_IMPLEMENTED, "stage4_other_app_route_pending")
            return
        }

        if (scope != "owned_track") {
            sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, "unknown_route_scope")
            return
        }

        val streamId = try {
            SrapCodec.requiredLong(params, "streamId")
        } catch (e: SrapProtocolException) {
            sendResponseError(sid, reqId, ProtocolError.INVALID_ARGUMENT, e.message)
            return
        }
        val leaseRes = snapshot.acquireLease(Capability.ROUTE_PLAYBACK, "owned_track", expectedRev)
        if (leaseRes is OperationResult.Failure) {
            sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = failureToBody(leaseRes)))
            return
        }
        val lease = (leaseRes as OperationResult.Done).value
        synchronized(stateLock) { pendingRequests += reqId }

        val orderLock = Any()
        var pendingSent = false
        var deferredReadback: RoutedDeviceReadback? = null

        fun emitRouted(readback: RoutedDeviceReadback) {
            val stillPending = synchronized(stateLock) {
                phase == Phase.AUTHENTICATED && sessionId == sid && pendingRequests.remove(reqId)
            }
            if (!stillPending) return
            val currentSnap = bridge.currentPermissionSnapshot()
            val leaseValid = lease.isValidFor(currentSnap, requireExactCapabilityRevision = false)
            if (!leaseValid) {
                sendEvent(
                    session = sid,
                    name = "audio.routed",
                    extra = mapOf(
                        "requestId" to StrictJson.str(reqId),
                        "targetKey" to StrictJson.str(targetKey),
                        "actualKey" to StrictJson.str(""),
                        "verified" to StrictJson.bool(false),
                        "error" to StrictJson.str(ProtocolError.PERMISSION_LOST.wireId),
                        "reason" to StrictJson.str("lease_invalidated")
                    )
                )
                return
            }
            sendEvent(
                session = sid,
                name = "audio.routed",
                extra = mapOf(
                    "requestId" to StrictJson.str(reqId),
                    "targetKey" to StrictJson.str(readback.targetKey),
                    "actualKey" to StrictJson.str(readback.actualSink?.key ?: ""),
                    "verified" to StrictJson.bool(readback.verified),
                    "reason" to StrictJson.str(readback.reason)
                )
            )
        }

        val accepted = bridge.requestOwnedTrackRoute(sid, expectedRev, streamId, reqId, targetKey) { readback ->
            val emitNow = synchronized(orderLock) {
                if (pendingSent) {
                    true
                } else {
                    deferredReadback = readback
                    false
                }
            }
            if (emitNow) emitRouted(readback)
        }

        when (accepted) {
            is OperationResult.Pending, is OperationResult.Done -> {
                sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = pendingResponseBody()))
                val buffered = synchronized(orderLock) {
                    pendingSent = true
                    val r = deferredReadback
                    deferredReadback = null
                    r
                }
                if (buffered != null) emitRouted(buffered)
            }
            is OperationResult.Failure -> {
                synchronized(stateLock) { pendingRequests.remove(reqId) }
                sendControl(SrapControlEnvelope(type = "response", id = reqId, session = sid, body = failureToBody(accepted)))
            }
        }
    }

    private fun handleCancel(sid: String, reqId: String?, body: JsonObject) {
        val cancelReqId = runCatching { SrapCodec.optionalText(body, "requestId") }.getOrNull()
        val cancelStreamId = if (body.has("streamId")) runCatching { SrapCodec.requiredLong(body, "streamId") }.getOrNull() else null
        clipboardRequests.cancel(cancelReqId, cancelStreamId)
        if (cancelReqId != null) {
            display.cancelRequest(cancelReqId)
            fileRequests.cancelRequest(cancelReqId)
            mediaRequests.cancel(cancelReqId)
            aliases?.cancel(sid, cancelReqId)
            synchronized(stateLock) { pendingRequests.remove(cancelReqId) }
        }
        if (cancelStreamId != null) {
            display.stop(cancelStreamId)
            capture.stop(cancelStreamId)
            fileRequests.cancelStream(cancelStreamId)
            synchronized(stateLock) { ownedStreamLeases.remove(cancelStreamId); pcmInputs.remove(cancelStreamId)?.close() }
            bridge.stopOwnedPlayback(sid, cancelStreamId)
        }
        if (reqId != null) {
            sendControl(
                SrapControlEnvelope(
                    type = "response",
                    id = reqId,
                    session = sid,
                    body = doneResponseBody(StrictJson.obj("canceled" to StrictJson.bool(true)))
                )
            )
        }
    }

    fun playbackFailed(sid: String, streamId: Long) {
        synchronized(stateLock) {
            if (sessionId != sid || ownedStreamLeases.remove(streamId) == null) return
            pcmInputs.remove(streamId)?.close()
        }
        sendEvent(sid, "stream.canceled", mapOf("streamId" to StrictJson.num(streamId),
            "error" to StrictJson.str(ProtocolError.TARGET_UNAVAILABLE.wireId)))
    }

    private fun volumeSystemJson(v: SystemVolumeSnapshot): JsonObject = StrictJson.obj(
        "scope" to StrictJson.str("system_media"),
        "streamIndex" to StrictJson.num(v.streamIndex),
        "minIndex" to StrictJson.num(v.minIndex),
        "maxIndex" to StrictJson.num(v.maxIndex),
        "percent" to StrictJson.num(v.percent),
        "isFixedVolume" to StrictJson.bool(v.isFixedVolume),
        "isMuted" to v.isMuted?.let(StrictJson::bool),
        "dndRestricted" to StrictJson.bool(v.dndRestricted)
    )

    private fun doneResponseBody(result: JsonObject): JsonObject = StrictJson.obj(
        "status" to StrictJson.str("done"),
        "result" to result
    )

    private fun pendingResponseBody(): JsonObject = StrictJson.obj(
        "status" to StrictJson.str("pending")
    )

    private fun errorResponseBody(
        error: ProtocolError,
        reason: String? = null,
        retryable: Boolean = false
    ): JsonObject = StrictJson.obj(
        "status" to StrictJson.str("error"),
        "error" to StrictJson.str(error.wireId),
        "reason" to reason?.takeIf { it.isNotEmpty() }?.take(128)?.let { StrictJson.str(it) },
        "retryable" to if (retryable) StrictJson.bool(true) else null
    )

    private fun failureToBody(f: OperationResult.Failure): JsonObject =
        errorResponseBody(f.error, f.reasonCode, f.retryable)

    private fun sendResponseError(
        sid: String,
        reqId: String,
        error: ProtocolError,
        reason: String? = null
    ) {
        sendControl(
            SrapControlEnvelope(
                type = "response",
                id = reqId,
                session = sid,
                body = errorResponseBody(error, reason)
            )
        )
    }

    private fun sendPairError(reqId: String, error: ProtocolError, reason: String?) {
        sendControl(
            SrapControlEnvelope(
                type = "pair.result",
                id = reqId,
                body = errorResponseBody(error, reason)
            )
        )
    }

    private fun sendSessionError(error: ProtocolError, reason: String? = null, session: String? = null) {
        val extra = LinkedHashMap<String, JsonValue>()
        extra["error"] = StrictJson.str(error.wireId)
        reason?.takeIf { it.isNotEmpty() }?.let { extra["reason"] = StrictJson.str(it.take(128)) }
        sendEvent(session = session, name = "session.error", extra = extra)
    }

    private fun sendEvent(session: String?, name: String, extra: Map<String, JsonValue> = emptyMap()): Boolean {
        val fields = LinkedHashMap<String, JsonValue>(1 + extra.size)
        fields["name"] = StrictJson.str(name)
        fields.putAll(extra)
        return synchronized(sendLock) { sendControl(
            SrapControlEnvelope(
                type = "event",
                session = session,
                seq = eventSeq++,
                body = JsonObject(fields)
            )
        ) }
    }

    private fun sendControl(envelope: SrapControlEnvelope): Boolean =
        synchronized(sendLock) {
            runCatching { writeDeadline.write { SrapCodec.writeFrame(output, SrapCodec.encodeControl(envelope)) } }.getOrDefault(false)
        }

    override fun close() {
        val sid: String?
        val fp: String?
        synchronized(stateLock) {
            if (phase == Phase.CLOSED) return
            phase = Phase.CLOSED
            sid = sessionId
            fp = peerFingerprint
            ownedStreamLeases.clear()
            incomingData.clear()
            pcmInputs.values.forEach(SrapPcmInput::close)
            pcmInputs.clear()
            pendingRequests.clear()
            idempotencyCache.clear()
        }
        notices.close()
        writeDeadline.close()
        runCatching { closeTransport() }
        capture.close()
        display.close()
        fileRequests.close()
        mediaRequests.close()
        clipboardRequests.close()
        if (sid != null) {
            aliases?.cancel(sid, reason = ProtocolError.STALE_SESSION)
            runCatching { bridge.cleanupSessionResources(sid) }
        }
        onClosed(sid, fp)
    }
}

private val CLIPBOARD_OPERATIONS = setOf("clipboard.watch", "clipboard.unwatch", "clipboard.get", "clipboard.set")
