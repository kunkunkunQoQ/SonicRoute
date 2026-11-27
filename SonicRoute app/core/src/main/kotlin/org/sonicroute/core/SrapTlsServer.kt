package org.sonicroute.core

import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.net.SocketException
import java.security.PrivateKey
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Timer
import java.util.TimerTask
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManager
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLPeerUnverifiedException
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager

data class SrapServerIdentity(
    val privateKey: PrivateKey,
    val certificate: X509Certificate
) {
    val spkiSha256Bytes: ByteArray = SrapCrypto.sha256(certificate.publicKey.encoded)
    val fingerprintSha256Upper: String = SrapCrypto.toHexUpper(spkiSha256Bytes)
}

/**
 * Reusable pure-JSSE TLS 1.3/1.2 SRAP v1.0 listener shared by Android runtime and JVM integration tests.
 * Uses wantClientAuth=true with SPKI public-key pinning verified immediately post-handshake in SrapServerConnection.
 */
class SrapTlsServer(
    private val identity: SrapServerIdentity,
    private val appVersion: String,
    private val peers: TrustedPeerRegistry,
    private val pairingWindow: PairingWindowController,
    private val bridge: SrapOperationBridge,
    private val bindAddress: InetAddress? = null,
    private val requestedPort: Int = 0,
    private val maxConcurrentConnections: Int = 6,
    private val onActiveSessionsChanged: (List<TrustedPeer>) -> Unit = {},
    onAliasChanged: (() -> Unit)? = null,
    elapsedMs: () -> Long = { TimeUnit.NANOSECONDS.toMillis(System.nanoTime()) }
) : Closeable {

    private var serverSocket: SSLServerSocket? = null
    private var acceptThread: Thread? = null
    private var connectionPool: ExecutorService? = null
    private val activeConnections = CopyOnWriteArrayList<SrapServerConnection>()
    private val authenticatedPeers = LinkedHashMap<String, TrustedPeer>()
    // Includes accepted sockets before JSSE handshake and before the first application frame.
    private val acceptedSockets = LinkedHashMap<SSLSocket, Timer?>()
    private val aliases = onAliasChanged?.let { SrapAliasBinding(identity, appVersion, it, elapsedMs) }
    @Volatile private var running = false
    @Volatile private var retiring = false

    val localPort: Int
        get() = serverSocket?.localPort ?: 0

    fun isRunning(): Boolean = running && !retiring

    fun retire() {
        retiring = true
        aliases?.cancel(reason = ProtocolError.STALE_SESSION)
    }

    fun aliasPrompt(): SrapAliasPrompt? = aliases?.prompt()
    fun decideAliasBinding(confirmationId: String, approve: Boolean) = aliases?.decide(confirmationId, approve)
    fun cancelAliasBinding() = aliases?.cancel()

    fun hasTrustedSession(sessionId: String): Boolean {
        val peer = synchronized(authenticatedPeers) { authenticatedPeers[sessionId] } ?: return false
        return isRunning() && peers.findByFingerprint(peer.fingerprintSha256) != null
    }

    fun uniqueSessionForPeer(fingerprint: String): String? {
        if (!isRunning() || peers.findByFingerprint(fingerprint) == null) return null
        return synchronized(authenticatedPeers) {
            authenticatedPeers.entries.singleOrNull { it.value.fingerprintSha256 == fingerprint }?.key
        }
    }

    @Synchronized
    fun start(): Int {
        if (running) return localPort
        val sslContext = buildServerSslContext(identity)
        val factory = sslContext.serverSocketFactory
        val srv = (if (bindAddress != null) {
            factory.createServerSocket(requestedPort, 8, bindAddress)
        } else {
            factory.createServerSocket(requestedPort, 8)
        }) as SSLServerSocket

        srv.reuseAddress = true
        srv.enabledProtocols = SrapCrypto.filterSupported(srv.supportedProtocols, SrapCrypto.ALLOWED_TLS_PROTOCOLS)
        val ciphers = SrapCrypto.filterSupported(srv.supportedCipherSuites, SrapCrypto.ALLOWED_TLS_CIPHERS)
        if (ciphers.isNotEmpty()) srv.enabledCipherSuites = ciphers
        srv.wantClientAuth = true

        serverSocket = srv
        retiring = false
        running = true
        val pool = Executors.newCachedThreadPool { r ->
            Thread(r, "SonicRoute-srap-conn").apply { isDaemon = true }
        }
        connectionPool = pool
        acceptThread = Thread({ acceptLoop(srv, pool) }, "SonicRoute-srap-accept").apply {
            isDaemon = true
            start()
        }
        return srv.localPort
    }

    fun broadcastCapabilities(snapshot: PermissionSnapshot) {
        for (conn in activeConnections) {
            conn.queueCapabilities(snapshot)
        }
    }

    fun notifyMediaChanged() {
        for (conn in activeConnections) conn.mediaChanged()
    }

    fun revokePeer(fingerprintSha256: String): Boolean {
        val removed = peers.removePeer(fingerprintSha256)
        pairingWindow.clearReceiptForPeer(fingerprintSha256)
        if (!removed) return false
        onPeerRemoved(fingerprintSha256)
        return true
    }

    fun onPeerRemoved(fingerprintSha256: String) {
        if (peers.findByFingerprint(fingerprintSha256) != null) return
        val upper = fingerprintSha256.uppercase()
        for (conn in activeConnections) {
            if (conn.peerFingerprint?.equals(upper, ignoreCase = true) == true) {
                conn.endSession(ProtocolError.PERMISSION_LOST, "peer_revoked")
            }
        }
    }

    fun disconnectAllSessions(reason: String = "server_stopping") {
        for (conn in activeConnections) {
            conn.endSession(ProtocolError.STALE_SESSION, reason)
        }
    }

    fun activeAuthenticatedPeers(): List<TrustedPeer> = synchronized(authenticatedPeers) {
        authenticatedPeers.values.toList()
    }

    fun playbackFailed(sessionId: String, streamId: Long) {
        activeConnections.firstOrNull { it.sessionId == sessionId }?.playbackFailed(sessionId, streamId)
    }

    private fun acceptLoop(srv: SSLServerSocket, pool: ExecutorService) {
        while (running && !srv.isClosed) {
            val socket = try {
                srv.accept() as SSLSocket
            } catch (_: SocketException) {
                break
            } catch (_: Throwable) {
                if (!running || srv.isClosed) break
                continue
            }
            synchronized(this) {
                if (isRunning() && serverSocket === srv && acceptedSockets.size < maxConcurrentConnections) {
                    acceptedSockets[socket] = null
                    setAdmissionDeadline(socket, HANDSHAKE_TIMEOUT_MS.toLong())
                    pool.execute { handleSocket(socket) }
                } else {
                    runCatching { socket.close() }
                }
            }
        }
    }

    @Synchronized
    private fun setAdmissionDeadline(socket: SSLSocket, delayMs: Long) {
        if (!running || !acceptedSockets.containsKey(socket)) throw IOException("receiver_closed")
        val timer = Timer("SonicRoute-srap-admission", true)
        acceptedSockets.put(socket, timer)?.cancel()
        timer.schedule(object : TimerTask() {
            override fun run() {
                synchronized(this@SrapTlsServer) {
                    if (acceptedSockets[socket] === timer) runCatching { socket.close() }
                }
                timer.cancel()
            }
        }, delayMs)
    }

    private fun handleSocket(socket: SSLSocket) {
        var connection: SrapServerConnection? = null
        try {
            socket.soTimeout = HANDSHAKE_TIMEOUT_MS
            socket.tcpNoDelay = true
            socket.keepAlive = true
            socket.startHandshake()

            val peerCertFp = try {
                val leaf = socket.session.peerCertificates.firstOrNull() as? X509Certificate
                    ?: throw CertificateException("missing_leaf")
                leaf.checkValidity()
                SrapCrypto.sha256HexUpper(leaf.publicKey.encoded)
            } catch (_: SSLPeerUnverifiedException) { null }

            connection = SrapServerConnection(
                input = socket.inputStream,
                output = socket.outputStream,
                closeTransport = { runCatching { socket.close() } },
                androidSpkiSha256Bytes = identity.spkiSha256Bytes,
                peerCertSpkiSha256Upper = peerCertFp,
                appVersion = appVersion,
                peers = peers,
                pairingWindow = pairingWindow,
                bridge = bridge,
                eventExecutor = connectionPool ?: throw IOException("receiver_closed"),
                receiverActive = ::isRunning,
                aliases = aliases,
                onEnding = { setAdmissionDeadline(socket, 1500) },
                onPairingOnly = { remaining ->
                    setAdmissionDeadline(socket, remaining)
                    socket.soTimeout = remaining.toInt()
                },
                onAuthenticated = { sid, peer ->
                    synchronized(this) {
                        if (!isRunning() || !acceptedSockets.containsKey(socket) || socket.isClosed ||
                            peers.findByFingerprint(peer.fingerprintSha256) == null
                        ) throw IOException("session_expired")
                        acceptedSockets.put(socket, null)?.cancel()
                        socket.soTimeout = 0
                        synchronized(authenticatedPeers) { authenticatedPeers[sid] = peer }
                    }
                    onActiveSessionsChanged(activeAuthenticatedPeers())
                },
                onClosed = { sid, _ ->
                    if (sid != null) {
                        synchronized(authenticatedPeers) { authenticatedPeers.remove(sid) }
                        onActiveSessionsChanged(activeAuthenticatedPeers())
                    }
                }
            )
            synchronized(this) {
                if (!running || !acceptedSockets.containsKey(socket)) return
                activeConnections += connection
            }
            connection.run()
        } catch (_: Throwable) {
            // Handshake or transport failure closes socket cleanly without logging secrets.
        } finally {
            connection?.let {
                activeConnections.remove(it)
                it.close()
            }
            runCatching { socket.close() }
            synchronized(this) { acceptedSockets.remove(socket)?.cancel() }
        }
    }

    @Synchronized
    override fun close() {
        if (!running) return
        running = false
        aliases?.cancel(reason = ProtocolError.STALE_SESSION)
        pairingWindow.closeWindow()
        runCatching { serverSocket?.close() }
        serverSocket = null
        for ((socket, timer) in acceptedSockets) {
            timer?.cancel()
            runCatching { socket.close() }
        }
        acceptedSockets.clear()
        for (conn in activeConnections.toList()) {
            conn.close()
        }
        activeConnections.clear()
        synchronized(authenticatedPeers) { authenticatedPeers.clear() }
        onActiveSessionsChanged(emptyList())
        connectionPool?.shutdownNow()
        connectionPool = null
        acceptThread = null
    }

    companion object {
        private const val HANDSHAKE_TIMEOUT_MS = 10_000

        fun buildServerSslContext(identity: SrapServerIdentity, expectedPeerPin: String? = null): SSLContext {
            val km = singleIdentityKeyManager(identity)
            // Pinning trust manager: TLS verifies client CertificateVerify possession of private key;
            // SrapServerConnection verifies exact SPKI SHA-256 against TrustedPeerRegistry immediately after handshake.
            val tm = pinningAcceptTrustManager(expectedPeerPin)
            val ctx = SSLContext.getInstance("TLS")
            ctx.init(arrayOf<KeyManager>(km), arrayOf<TrustManager>(tm), null)
            return ctx
        }

        fun buildClientSslContext(
            clientIdentity: SrapServerIdentity?,
            expectedServerFingerprintUpper: String
        ): SSLContext {
            val kms = clientIdentity?.let { arrayOf<KeyManager>(singleIdentityKeyManager(it)) }
            val expectedUpper = expectedServerFingerprintUpper.uppercase()
            val tm = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                    throw CertificateException("This trust manager authenticates servers only")
                }
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                    val leaf = chain?.firstOrNull()
                        ?: throw java.security.cert.CertificateException("Missing server certificate")
                    leaf.checkValidity()
                    val actualFp = SrapCrypto.sha256HexUpper(leaf.publicKey.encoded)
                    if (actualFp != expectedUpper) {
                        throw java.security.cert.CertificateException("Server SPKI fingerprint mismatch")
                    }
                }
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }
            val ctx = SSLContext.getInstance("TLS")
            ctx.init(kms, arrayOf<TrustManager>(tm), null)
            return ctx
        }

        private fun singleIdentityKeyManager(identity: SrapServerIdentity): X509ExtendedKeyManager {
            val chain = arrayOf(identity.certificate)
            return object : X509ExtendedKeyManager() {
                override fun getClientAliases(keyType: String?, issuers: Array<out java.security.Principal>?) =
                    if (keyType == "EC") arrayOf("sr") else null
                override fun chooseClientAlias(
                    keyType: Array<out String>?,
                    issuers: Array<out java.security.Principal>?,
                    socket: java.net.Socket?
                ): String? = if (keyType?.contains("EC") == true) "sr" else null
                override fun getServerAliases(keyType: String?, issuers: Array<out java.security.Principal>?) =
                    if (keyType == "EC") arrayOf("sr") else null
                override fun chooseServerAlias(
                    keyType: String?,
                    issuers: Array<out java.security.Principal>?,
                    socket: java.net.Socket?
                ): String? = if (keyType == "EC") "sr" else null
                override fun getCertificateChain(alias: String?) = if (alias == "sr") chain else null
                override fun getPrivateKey(alias: String?) = if (alias == "sr") identity.privateKey else null
            }
        }

        private fun pinningAcceptTrustManager(expectedPeerPin: String?): X509TrustManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val leaf = chain?.firstOrNull()
                    ?: throw java.security.cert.CertificateException("Empty client certificate chain")
                leaf.checkValidity()
                SrapCrypto.parseEcP256Spki(leaf.publicKey.encoded)
                if (expectedPeerPin != null && SrapCrypto.sha256HexUpper(leaf.publicKey.encoded) != expectedPeerPin) {
                    throw CertificateException("peer_pin_mismatch")
                }
            }
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                throw CertificateException("This trust manager authenticates clients only")
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
    }
}
