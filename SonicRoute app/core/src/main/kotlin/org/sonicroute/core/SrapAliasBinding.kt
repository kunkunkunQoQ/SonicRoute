package org.sonicroute.core

import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.cert.X509Certificate
import java.util.Timer
import java.util.TimerTask
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLSocket

data class SrapAliasPrompt(
    val confirmationId: String,
    val displayName: String,
    val peerFingerprintSha256: String,
    val awaitingProof: Boolean
)

internal class SrapAliasTranscript(
    private val bindId: ByteArray,
    private val challenge: ByteArray,
    private val androidPin: ByteArray,
    private val windowsPin: ByteArray,
    private val profileRef: ByteArray,
    private val attachment: ByteArray,
    private val parentSession: String,
    private val port: Int,
    private val major: Int,
    private val minor: Int
) {
    fun encode(role: Int): ByteArray {
        require(role == WINDOWS || role == ANDROID)
        require(bindId.size == 16 && challenge.size == 32 && androidPin.size == 32 && windowsPin.size == 32)
        require(profileRef.size == 32 && attachment.size == 16 && parentSession.length == 32)
        require(parentSession.all { it in '0'..'9' || it in 'a'..'f' })
        require(port in 1..65535 && major in 0..65535 && minor in 0..65535)
        val bytes = ByteBuffer.allocate(195).order(ByteOrder.BIG_ENDIAN)
        bytes.put("SRAP1/ALIAS".toByteArray(Charsets.US_ASCII)).put(0).put(role.toByte())
        bytes.put(bindId).put(challenge).put(androidPin).put(windowsPin).put(profileRef).put(attachment)
        for (i in parentSession.indices step 2) bytes.put(parentSession.substring(i, i + 2).toInt(16).toByte())
        bytes.putShort(port.toShort()).putShort(major.toShort()).putShort(minor.toShort())
        return bytes.array()
    }

    companion object {
        const val WINDOWS = 1
        const val ANDROID = 2
    }
}

/** One confirmed ADB co-presence proof per running receiver. No ADB commands or privileged APIs. */
internal class SrapAliasBinding(
    private val identity: SrapServerIdentity,
    private val appVersion: String,
    private val onChanged: () -> Unit,
    private val clockMs: () -> Long = { TimeUnit.NANOSECONDS.toMillis(System.nanoTime()) },
    private val consentMs: Long = 60_000,
    private val proofMs: Long = 30_000
) {
    class Parent(
        val session: String,
        val requestId: String,
        val minor: Int,
        val peer: TrustedPeer,
        val checkAccess: () -> ProtocolError?,
        val respond: (OperationResult<JsonObject>) -> Boolean,
        val sendProof: (Map<String, JsonValue>) -> Boolean
    )

    private class Window(
        val parent: Parent,
        val profileRef: ByteArray,
        val attachment: ByteArray,
        val confirmationId: String = SrapCrypto.randomSessionId()
    ) {
        val consent = CountDownLatch(1)
        val timer = Timer("SonicRoute-alias-expiry", true)
        var expiry: TimerTask? = null
        var deadline = 0L
        var approved = false
        var stopped: ProtocolError? = null
        var listener: ServerSocket? = null
        var socket: Socket? = null
    }

    private class Failure(val code: ProtocolError) : Exception()
    private val lock = Any()
    private var active: Window? = null

    fun prompt(): SrapAliasPrompt? = synchronized(lock) {
        val w = active ?: return null
        if (w.stopped != null) return null
        SrapAliasPrompt(w.confirmationId, w.parent.peer.displayName, w.parent.peer.fingerprintSha256, w.approved)
    }

    fun begin(parent: Parent, params: JsonObject): ProtocolError? {
        if (params.fields.keys != setOf("profileRef", "attachment")) return ProtocolError.INVALID_ARGUMENT
        val profile = fixedBytes(params, "profileRef", 32) ?: return ProtocolError.INVALID_ARGUMENT
        val attachment = fixedBytes(params, "attachment", 16) ?: return ProtocolError.INVALID_ARGUMENT
        synchronized(lock) {
            if (active != null) return ProtocolError.BUSY
            parent.checkAccess()?.let { return it }
            val window = Window(parent, profile, attachment)
            active = window
            armLocked(window, consentMs)
            Thread({ serve(window) }, "SonicRoute-alias-proof").apply { isDaemon = true; start() }
        }
        onChanged()
        return null
    }

    fun decide(confirmationId: String, approve: Boolean) {
        synchronized(lock) {
            val w = active ?: return
            if (w.confirmationId != confirmationId || w.stopped != null) return
            when {
                clockMs() >= w.deadline -> stopLocked(w, ProtocolError.TIMEOUT)
                !approve -> stopLocked(w, ProtocolError.CANCELED)
                !w.approved -> {
                    w.approved = true
                    armLocked(w, proofMs)
                    w.consent.countDown()
                }
                else -> return
            }
        }
        onChanged()
    }

    fun cancel(session: String? = null, requestId: String? = null, reason: ProtocolError = ProtocolError.CANCELED) {
        synchronized(lock) {
            val w = active ?: return
            if (session != null && w.parent.session != session) return
            if (requestId != null && w.parent.requestId != requestId) return
            stopLocked(w, reason)
        }
        onChanged()
    }

    fun revalidate(session: String) {
        synchronized(lock) {
            val w = active ?: return
            if (w.parent.session != session) return
            val error = w.parent.checkAccess() ?: return
            stopLocked(w, error)
        }
        onChanged()
    }

    private fun armLocked(w: Window, duration: Long) {
        w.expiry?.cancel()
        w.deadline = clockMs() + duration
        val task = object : TimerTask() {
            override fun run() {
                synchronized(lock) {
                    if (active !== w || w.expiry !== this || w.stopped != null) return
                    stopLocked(w, ProtocolError.TIMEOUT)
                }
                onChanged()
            }
        }
        w.expiry = task
        w.timer.schedule(task, duration)
    }

    private fun stopLocked(w: Window, error: ProtocolError) {
        if (w.stopped != null) return
        w.stopped = error
        w.consent.countDown()
        // Close the underlying TCP socket to interrupt every TLS handshake/read, even a partial frame.
        runCatching { w.listener?.close() }
        runCatching { w.socket?.close() }
        w.timer.cancel()
        if (active === w) active = null
    }

    private fun checkLocked(w: Window) {
        w.stopped?.let { throw Failure(it) }
        if (active !== w) throw Failure(ProtocolError.STALE_SESSION)
        if (clockMs() >= w.deadline) throw Failure(ProtocolError.TIMEOUT)
        w.parent.checkAccess()?.let { throw Failure(it) }
    }

    private class Challenge(val bindId: ByteArray, val nonce: ByteArray, val port: Int) {
        val text = SrapCrypto.base64UrlEncodeNoPad(bindId)
        fun result(duration: Long) = StrictJson.obj(
            "bindId" to StrictJson.str(text),
            "challenge" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(nonce)),
            "port" to StrictJson.num(port),
            "expiresInMs" to StrictJson.num(duration)
        )
    }

    private fun serve(w: Window) {
        var issued = false
        try {
            w.consent.await()
            synchronized(lock) { checkLocked(w) }
            val tlsFactory = SrapTlsServer.buildServerSslContext(identity, w.parent.peer.fingerprintSha256).socketFactory
            val event = ServerSocket().use { listener ->
                val challenge = synchronized(lock) {
                    checkLocked(w)
                    w.listener = listener
                    listener.bind(InetSocketAddress(LOOPBACK, 0), 1)
                    armLocked(w, proofMs)
                    Challenge(SrapCrypto.randomBytes(16), SrapCrypto.randomBytes(32), listener.localPort)
                }
                if (!w.parent.respond(OperationResult.Done(challenge.result(proofMs)))) throw IOException()
                issued = true
                listener.accept().use { raw ->
                    synchronized(lock) {
                        checkLocked(w)
                        w.socket = raw
                        listener.close()
                    }
                    val tls = tlsFactory.createSocket(raw, LOOPBACK.hostAddress, raw.port, true) as SSLSocket
                    tls.use { receiveProof(w, challenge, it) }
                }
            }
            // Both proof sockets have closed. Consume under the same lock used by cancel and expiry.
            synchronized(lock) {
                checkLocked(w)
                active = null
                w.timer.cancel()
            }
            onChanged()
            w.parent.sendProof(event)
        } catch (e: Exception) {
            val error = synchronized(lock) { w.stopped } ?: (e as? Failure)?.code ?: ProtocolError.IO_FAILURE
            if (!issued) runCatching { w.parent.respond(OperationResult.Failure(error)) }
        } finally {
            synchronized(lock) {
                w.timer.cancel()
                if (active === w) active = null
            }
            onChanged()
        }
    }

    private fun receiveProof(w: Window, challenge: Challenge, tls: SSLSocket): Map<String, JsonValue> {
        tls.useClientMode = false
        tls.enabledProtocols = SrapCrypto.filterSupported(tls.supportedProtocols, SrapCrypto.ALLOWED_TLS_PROTOCOLS)
        tls.enabledCipherSuites = SrapCrypto.filterSupported(tls.supportedCipherSuites, SrapCrypto.ALLOWED_TLS_CIPHERS)
        tls.needClientAuth = true
        tls.startHandshake()
        val leaf = tls.session.peerCertificates.firstOrNull() as? X509Certificate ?: throw Failure(ProtocolError.PERMISSION_LOST)
        val key = leaf.publicKey.encoded
        if (SrapCrypto.sha256HexUpper(key) != w.parent.peer.fingerprintSha256) throw Failure(ProtocolError.IDENTITY_CONFLICT)
        val hello = readControl(tls)
        if (hello.type != "hello" || hello.session != null || hello.seq != null) throw Failure(ProtocolError.INVALID_ARGUMENT)
        if (SrapCodec.negotiateMinor(SrapCodec.parseHello(hello.body)) != w.parent.minor) {
            writeControl(tls, SrapControlEnvelope("event", StrictJson.obj(
                "name" to StrictJson.str("session.error"), "error" to StrictJson.str(ProtocolError.VERSION_MISMATCH.wireId)
            ), seq = 0))
            throw Failure(ProtocolError.VERSION_MISMATCH)
        }
        writeControl(tls, SrapControlEnvelope("hello", SrapCodec.encodeHello(appVersion = appVersion), id = hello.id))
        val prove = readControl(tls)
        try {
            return verifyAndSign(w, challenge, key, prove)
        } catch (e: Exception) {
            val code = (e as? Failure)?.code ?: ProtocolError.INVALID_ARGUMENT
            if (prove.id != null) runCatching {
                writeControl(tls, SrapControlEnvelope("alias.bind.result", StrictJson.obj(
                    "status" to StrictJson.str("error"), "error" to StrictJson.str(code.wireId)
                ), id = prove.id))
            }
            throw e
        }
    }

    private fun verifyAndSign(w: Window, challenge: Challenge, key: ByteArray, prove: SrapControlEnvelope): Map<String, JsonValue> {
        if (prove.type != "alias.bind.prove" || prove.id == null || prove.session != null || prove.seq != null ||
            prove.body.fields.keys != setOf("bindId", "profileRef", "attachment", "parentSession", "signature")
        ) throw Failure(ProtocolError.INVALID_ARGUMENT)
        val profileText = SrapCrypto.base64UrlEncodeNoPad(w.profileRef)
        val attachmentText = SrapCrypto.base64UrlEncodeNoPad(w.attachment)
        if (SrapCodec.requiredText(prove.body, "bindId") != challenge.text ||
            SrapCodec.requiredText(prove.body, "profileRef") != profileText ||
            SrapCodec.requiredText(prove.body, "attachment") != attachmentText ||
            SrapCodec.requiredText(prove.body, "parentSession") != w.parent.session
        ) throw Failure(ProtocolError.IDENTITY_CONFLICT)
        val signature = SrapCrypto.base64UrlDecodeNoPad(SrapCodec.requiredText(prove.body, "signature"))
        val transcript = SrapAliasTranscript(challenge.bindId, challenge.nonce, identity.spkiSha256Bytes,
            SrapCrypto.sha256(key), w.profileRef, w.attachment, w.parent.session, challenge.port, SrapCodec.MAJOR, w.parent.minor)
        if (!SrapCrypto.verifyEcdsaP256Sha256(key, transcript.encode(SrapAliasTranscript.WINDOWS), signature)) {
            throw Failure(ProtocolError.IDENTITY_CONFLICT)
        }
        synchronized(lock) { checkLocked(w) }
        val receipt = SrapCrypto.signEcdsaP256Sha256(identity.privateKey, transcript.encode(SrapAliasTranscript.ANDROID))
        return mapOf(
            "bindId" to StrictJson.str(challenge.text), "requestId" to StrictJson.str(w.parent.requestId),
            "profileRef" to StrictJson.str(profileText), "attachment" to StrictJson.str(attachmentText),
            "signature" to StrictJson.str(SrapCrypto.base64UrlEncodeNoPad(receipt))
        )
    }

    companion object {
        private val LOOPBACK = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))
        private fun fixedBytes(obj: JsonObject, name: String, size: Int) = runCatching {
            SrapCrypto.base64UrlDecodeNoPad(SrapCodec.requiredText(obj, name)).takeIf { it.size == size }
        }.getOrNull()

        private fun readControl(tls: SSLSocket) = SrapCodec.parseControl(SrapCodec.readFrame(tls.inputStream) ?: throw IOException())
        private fun writeControl(tls: SSLSocket, env: SrapControlEnvelope) =
            SrapCodec.writeFrame(tls.outputStream, SrapCodec.encodeControl(env))
    }
}
