package org.sonicroute.platform

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import org.sonicroute.core.AppCatalogSnapshot
import org.sonicroute.core.AudioOutputSink
import org.sonicroute.core.AudioSinkNormalizer
import org.sonicroute.core.OperationResult
import org.sonicroute.core.PairingWindowController
import org.sonicroute.core.PendingPairingRequest
import org.sonicroute.core.PermissionSnapshot
import org.sonicroute.core.MediaPlayerSnapshot
import org.sonicroute.core.SrapPcmInput
import org.sonicroute.core.ProtocolError
import org.sonicroute.core.RoutedDeviceReadback
import org.sonicroute.core.SrapCodec
import org.sonicroute.core.SrapAliasPrompt
import org.sonicroute.core.SrapCrypto
import org.sonicroute.core.SrapOperationBridge
import org.sonicroute.core.SrapPairingInvite
import org.sonicroute.core.SrapServerIdentity
import org.sonicroute.core.SrapTlsServer
import org.sonicroute.core.SystemVolumeSnapshot
import org.sonicroute.core.TrustedPeer
import java.net.Inet4Address
import java.net.InetAddress
import java.util.concurrent.Executors

data class LanServerState(
    val enabled: Boolean = false,
    val listening: Boolean = false,
    val waitingForLan: Boolean = false,
    val port: Int = 0,
    val localIp: String? = null,
    val androidFingerprintSha256: String = "",
    val nsdServiceName: String? = null,
    val nsdRegistered: Boolean = false,
    val activeInvite: SrapPairingInvite? = null,
    val pendingPairing: PendingPairingRequest? = null,
    val aliasBinding: SrapAliasPrompt? = null,
    val trustedPeers: List<TrustedPeer> = emptyList(),
    val connectedPeers: List<TrustedPeer> = emptyList(),
    val lastError: String? = null
)

/**
 * Owns Android SRAP v1.0 TLS listener, ephemeral NSD (_sonicroute._tcp), Wi-Fi/Ethernet-scoped
 * NetworkCallback recovery, 120 s pairing window, Android Keystore identity, and trusted peer store.
 *
 * Key power & network guarantees:
 * - Default OFF until explicitly enabled by user.
 * - Uses a dedicated WIFI|ETHERNET NetworkRequest so leaving Wi-Fi onto cellular cleanly tears down
 *   LAN sockets & NSD (WAITING_FOR_LAN, never listening on cellular) and returning to Wi-Fi automatically
 *   re-binds TLS + re-advertises NSD for trusted mTLS reconnection without re-pairing.
 * - Zero wake locks, zero periodic polling, all socket writes run off the main looper.
 */
class LanServerController(
    context: Context,
    private val appVersion: String,
    private val permissionSnapshotProvider: () -> PermissionSnapshot,
    private val audioController: LocalAudioController,
    private val onStateChanged: (LanServerState) -> Unit,
    private val onLanAvailabilityChanged: (Boolean) -> Unit = {},
    private val onActiveSessionCountChanged: (Int) -> Unit = {},
    private val onRemoteAudioActiveChanged: (Boolean) -> Unit = {},
    private val readSharedMedia: () -> OperationResult<List<MediaPlayerSnapshot>> =
        { OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED) },
    private val readSharedMediaPackageState: (String) -> OperationResult<Boolean> =
        { OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED) },
    private val sharedClipboard: LocalSharedClipboard? = null,
    private val sharedCapture: SharedPlaybackCapture? = null,
    private val sharedScreen: SharedScreenProjection? = null,
    private val fileStore: org.sonicroute.core.SharedFileStore? = null,
    private val controlSharedMedia: (String, String, Long?, (OperationResult<MediaPlayerSnapshot>) -> Unit) -> AutoCloseable =
        { _, _, _, done -> done(OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)); AutoCloseable {} }
) : AutoCloseable {

    private val appContext = context.applicationContext
    private val nsdManager = appContext.getSystemService(Context.NSD_SERVICE) as? NsdManager
    private val connectivityManager = appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor { r ->
        Thread(r, "SonicRoute-lan-ctrl").apply { isDaemon = true }
    }

    private val peerStore = FileTrustedPeerStore(appContext)
    private var pairingWindow = PairingWindowController()

    @Volatile private var identity: SrapServerIdentity? = null
    @Volatile private var tlsServer: SrapTlsServer? = null
    private var nsdListener: NsdManager.RegistrationListener? = null
    private var nsdInstanceToken: String? = null
    private var nsdRegistered = false
    private var nsdServiceName: String? = null
    private var lanCallback: ConnectivityManager.NetworkCallback? = null
    private var bindingNetwork: Network? = null
    private var bindingIp: String? = null
    @Volatile private var bindingGeneration = 0L
    private var pairingRequested = false
    private var advertisedEndpoint: Triple<Network?, Int, Boolean>? = null
    private val activeLanNetworks = LinkedHashMap<Network, String?>()

    private var state = LanServerState(trustedPeers = peerStore.listPeers())
    @Volatile private var closed = false
    private var enabled = false

    private var streamOwnerSessionId: String? = null
    private var streamOwnerStreamId: Long = 0L
    private var pendingRouteId: String? = null
    private val mediaChangeQueued = java.util.concurrent.atomic.AtomicBoolean(false)

    private val pairingExpiryRunnable = Runnable {
        if (!closed) {
            pairingWindow.closeWindow()
            updateNsdAdvertisement()
            publish()
        }
    }


    private val operationBridge = object : SrapOperationBridge {
        override fun grantsDisplay(session: String, peer: String) = sharedScreen?.granted(session, peer) == true
        override fun openDisplay(session: String, peer: String, revision: Long) = runAudioCommand(session, revision) {
            sharedScreen?.open(session, peer) ?: OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
        }
        override fun grantsCapture(session: String, peer: String) = sharedCapture?.granted(session, peer) == true
        override fun openCapture(session: String, revision: Long, peer: String, app: String) =
            runAudioCommand(session, revision) {
                sharedCapture?.open(session, peer, app) ?: OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
            }
        override val sharedFiles get() = fileStore
        override fun watchClipboard(session: String, capabilities: Long, watchId: String, changed: () -> Unit) =
            runAudioCommand(session, capabilities) { sharedClipboard?.watch(session, watchId, changed) ?: unavailableClipboard() }
        override fun readClipboard(session: String, capabilities: Long, watchId: String) =
            runAudioCommand(session, capabilities) { sharedClipboard?.read(session, watchId) ?: unavailableClipboard() }
        override fun writeClipboard(session: String, capabilities: Long, watchId: String, expected: Long, text: String) =
            runAudioCommand(session, capabilities) { sharedClipboard?.write(session, watchId, expected, text) ?: unavailableClipboard() }
        override fun unwatchClipboard(session: String, watchId: String) { main.post { sharedClipboard?.unwatch(session, watchId) } }
        private fun unavailableClipboard() = OperationResult.Failure(ProtocolError.NOT_IMPLEMENTED)
        override fun readMedia(sessionId: String, revision: Long) =
            runAudioCommand(sessionId, revision, readSharedMedia)

        override fun readMediaPackageState(sessionId: String, revision: Long, packageName: String) =
            runAudioCommand(sessionId, revision) { readSharedMediaPackageState(packageName) }

        override fun controlMedia(sessionId: String, revision: Long, key: String, action: String, position: Long?,
            completed: (OperationResult<MediaPlayerSnapshot>) -> Unit): OperationResult<AutoCloseable> =
            runAudioCommand(sessionId, revision) {
                val command = controlSharedMedia(key, action, position) { result ->
                    if (!closed) worker.execute { completed(result) }
                }
                OperationResult.Done(AutoCloseable { main.post { command.close() } })
            }

        override fun currentPermissionSnapshot(): PermissionSnapshot = permissionSnapshotProvider()
        override fun listAudioOutputs(): List<AudioOutputSink> = audioController.currentState().outputs
        override fun listAppCatalog(): AppCatalogSnapshot? = audioController.currentState().let {
            if (it.catalogState == LauncherCatalogState.READY) it.appCatalog else null
        }
        override fun readSystemMediaVolume(): SystemVolumeSnapshot = audioController.currentState().volume
        override fun writeSystemMediaVolume(sessionId: String, expectedRevision: Long, percent: Int) =
            runAudioCommand(sessionId, expectedRevision) {
                audioController.setSystemMediaVolumePercent(percent)
            }
        override fun writeSystemMediaVolumeIndex(sessionId: String, expectedRevision: Long, index: Int) =
            runAudioCommand(sessionId, expectedRevision) { audioController.setSystemMediaVolumeIndex(index) }

        override fun startOwnedPlayback(sessionId: String, expectedRevision: Long, streamId: Long, targetKey: String) =
            startPlayback(sessionId, expectedRevision, streamId, targetKey, null)

        override fun startNetworkPlayback(sessionId: String, expectedRevision: Long, streamId: Long,
            targetKey: String, pcm: SrapPcmInput) = startPlayback(sessionId, expectedRevision, streamId, targetKey, pcm)

        private fun startPlayback(sessionId: String, expectedRevision: Long, streamId: Long, targetKey: String, pcm: SrapPcmInput?) =
            runAudioCommand(sessionId, expectedRevision) {
                if (!isOutputAvailable(targetKey)) return@runAudioCommand outputUnavailable()
                if (streamOwnerSessionId != null) {
                    return@runAudioCommand OperationResult.Failure(ProtocolError.BUSY, "owned_track_in_use")
                }
                streamOwnerSessionId = sessionId
                streamOwnerStreamId = streamId
                onRemoteAudioActiveChanged(true)
                if (pcm == null) audioController.startTestTone(targetKey)
                else audioController.startNetworkPcm(targetKey, pcm)
                val playback = audioController.currentState().playback
                val error = playback.error
                if (error != null) {
                    releasePlayback(sessionId)
                    return@runAudioCommand OperationResult.Failure(error, "playback_start_failed")
                }
                OperationResult.Done(playback)
            }

        override fun stopOwnedPlayback(sessionId: String, streamId: Long): Boolean =
            onAudioThread(false) {
                if (ownsStream(sessionId, streamId)) releasePlayback(sessionId) else false
            }

        override fun readOwnedTrackVolume(sessionId: String, streamId: Long): OperationResult<Pair<Int, Boolean>> =
            onAudioThread<OperationResult<Pair<Int, Boolean>>>(audioTimeout()) {
                if (!ownsStream(sessionId, streamId)) return@onAudioThread notOwned()
                val playback = audioController.currentState().playback
                OperationResult.Done(playback.trackGainPercent to playback.trackMuted)
            }

        override fun writeOwnedTrackVolume(
            sessionId: String, expectedRevision: Long, streamId: Long, gainPercent: Int?, muted: Boolean?
        ) = runAudioCommand(sessionId, expectedRevision) {
            if (!ownsStream(sessionId, streamId)) return@runAudioCommand notOwned()
            if (gainPercent != null) audioController.setOwnedTrackGainPercent(gainPercent)
            if (muted != null) audioController.setOwnedTrackMuted(muted)
            val playback = audioController.currentState().playback
            OperationResult.Done(playback.trackGainPercent to playback.trackMuted)
        }

        override fun requestOwnedTrackRoute(
            sessionId: String, expectedRevision: Long, streamId: Long, requestId: String, targetKey: String,
            onRouted: (RoutedDeviceReadback) -> Unit
        ): OperationResult<Unit> = runAudioCommand(sessionId, expectedRevision) {
            if (!ownsStream(sessionId, streamId)) return@runAudioCommand notOwned()
            if (!isOutputAvailable(targetKey)) return@runAudioCommand outputUnavailable()
            if (pendingRouteId != null) {
                return@runAudioCommand OperationResult.Failure(ProtocolError.BUSY, "route_change_in_progress")
            }
            pendingRouteId = requestId
            audioController.requestOwnedRouteWithReadback(targetKey) { readback ->
                if (ownsStream(sessionId, streamId) && pendingRouteId == requestId) {
                    pendingRouteId = null
                    worker.execute { if (!closed) onRouted(readback) }
                }
            }
            OperationResult.Pending()
        }

        override fun cleanupSessionResources(sessionId: String) {
            main.post { releasePlayback(sessionId); sharedCapture?.endSession(sessionId); sharedScreen?.endSession(sessionId) }
        }
    }

    private fun ownsStream(sessionId: String, streamId: Long) =
        streamOwnerSessionId == sessionId && streamOwnerStreamId == streamId

    private fun releasePlayback(sessionId: String): Boolean {
        if (streamOwnerSessionId != sessionId) return false
        streamOwnerSessionId = null
        streamOwnerStreamId = 0L
        pendingRouteId = null
        audioController.stopTestTone()
        onRemoteAudioActiveChanged(false)
        return true
    }

    private fun isOutputAvailable(target: String) = target.equals(AudioOutputSink.KEY_CURRENT, true) ||
        AudioSinkNormalizer.findSink(audioController.currentState().outputs, target) != null

    private fun outputUnavailable() = OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "target_not_connected")
    private fun notOwned() = OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "stream_not_owned")
    private fun audioTimeout() = OperationResult.Failure(ProtocolError.TIMEOUT, "audio_command_timeout")

    private fun <T> runAudioCommand(sessionId: String, revision: Long, action: () -> OperationResult<T>): OperationResult<T> =
        onAudioThread<OperationResult<T>>(audioTimeout()) {
            when {
                closed || !enabled -> OperationResult.Failure(ProtocolError.STALE_SESSION, "listener_inactive")
                tlsServer?.hasTrustedSession(sessionId) != true -> OperationResult.Failure(ProtocolError.STALE_SESSION, "session_not_current")
                permissionSnapshotProvider().capabilityRevision != revision ->
                    OperationResult.Failure(ProtocolError.STALE_SESSION, "capability_revision_mismatch")
                else -> action()
            }
        }

    // All ownership changes, Android writes, readbacks and UI notifications share one looper.
    private fun <T> onAudioThread(timeout: T, action: () -> T): T {
        if (Looper.myLooper() == main.looper) return action()
        val task = java.util.concurrent.FutureTask(action)
        if (!main.post(task)) return timeout
        return try {
            task.get(3, java.util.concurrent.TimeUnit.SECONDS)
        } catch (_: java.util.concurrent.TimeoutException) {
            timeout
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            timeout
        } finally {
            main.removeCallbacks(task)
            task.cancel(false)
        }
    }

    fun initIdentityAsync() {
        worker.execute {
            if (closed) return@execute
            val id = runCatching { AndroidKeystoreIdentity.getOrCreate() }.getOrNull()
            main.post {
                if (!closed && id != null) {
                    identity = id
                    state = state.copy(
                        androidFingerprintSha256 = id.fingerprintSha256Upper,
                        trustedPeers = peerStore.listPeers()
                    )
                    publish()
                }
            }
        }
    }

    fun currentState(): LanServerState = state

    fun uniqueSessionForPeer(fingerprint: String) = tlsServer?.uniqueSessionForPeer(fingerprint)
    fun hasTrustedSession(sessionId: String) = tlsServer?.hasTrustedSession(sessionId) == true

    private fun currentDefaultLan(): Pair<Network, String>? {
        val cm = connectivityManager ?: return null
        val network = cm.activeNetwork ?: return null
        val caps = cm.getNetworkCapabilities(network) ?: return null
        if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) ||
            !(caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))) return null
        val ip = extractIpv4FromLinkProperties(cm.getLinkProperties(network)) ?: return null
        return network to ip
    }

    fun probeLanAvailableNow(): Boolean = currentDefaultLan() != null || bindingIp != null

    fun enableLan(openPairing: Boolean) {
        if (closed) return
        if (enabled) {
            if (openPairing) openPairingWindow()
            return
        }
        enabled = true
        state = state.copy(enabled = true, waitingForLan = true, lastError = null)
        if (!registerLanNetworkCallbackIfNeeded()) {
            state = state.copy(lastError = "network_callback_unavailable")
            onLanAvailabilityChanged(false)
            publish()
            return
        }
        currentDefaultLan()?.let { (network, ip) ->
            pairingRequested = openPairing
            onLanNetworkUpdated(network, ip, removed = false)
        } ?: run {
            onLanAvailabilityChanged(false)
            publish()
        }
    }

    fun openPairingWindow() {
        if (closed || !enabled || bindingIp == null) return
        pairingRequested = true
        if (tlsServer != null) publishListeningEndpoint()
        else if (state.lastError != null) {
            val network = bindingNetwork ?: return
            val ip = bindingIp ?: return
            bindServerAsync(network, ip)
        }
    }

    fun closePairingWindow() {
        pairingRequested = false
        main.removeCallbacks(pairingExpiryRunnable)
        val wasOpen = pairingWindow.isWindowOpen() || pairingWindow.currentPendingRequest() != null
        pairingWindow.closeWindow()
        if (wasOpen || state.activeInvite != null || state.pendingPairing != null) {
            state = state.copy(activeInvite = null, pendingPairing = null)
            updateNsdAdvertisement()
            publish()
        }
    }

    fun decidePendingPairing(approve: Boolean) {
        pairingWindow.decidePending(approve)
    }

    fun decideAliasBinding(confirmationId: String, approve: Boolean) {
        tlsServer?.decideAliasBinding(confirmationId, approve)
    }

    fun cancelAliasBinding() {
        tlsServer?.cancelAliasBinding()
    }

    fun revokePeer(fingerprintSha256: String) {
        if (closed) return
        worker.execute {
            val result = runCatching {
                if (peerStore.removePeer(fingerprintSha256)) {
                    pairingWindow.clearReceiptForPeer(fingerprintSha256)
                    tlsServer?.onPeerRemoved(fingerprintSha256)
                }
            }
            main.post {
                if (closed) return@post
                val active = tlsServer?.activeAuthenticatedPeers().orEmpty()
                state = state.copy(trustedPeers = peerStore.listPeers(), connectedPeers = active,
                    lastError = if (result.isFailure) "trust_store_write_failed" else null)
                onActiveSessionCountChanged(active.size)
                publish()
            }
        }
    }

    fun onPermissionSnapshotChanged(snapshot: PermissionSnapshot) {
        if (!closed) {
            val srv = tlsServer ?: return
            worker.execute {
                if (!closed) runCatching { srv.broadcastCapabilities(snapshot) }
            }
        }
    }

    fun onMediaSessionsChanged() {
        val server = tlsServer ?: return
        if (closed || !mediaChangeQueued.compareAndSet(false, true)) return
        worker.execute {
            mediaChangeQueued.set(false)
            if (!closed && server === tlsServer) server.notifyMediaChanged()
        }
    }

    fun onRemotePlaybackFailed() {
        val session = streamOwnerSessionId ?: return
        val stream = streamOwnerStreamId
        val server = tlsServer
        main.post {
            if (ownsStream(session, stream) && audioController.currentState().playback.error != null) {
                releasePlayback(session)
                if (!closed && server != null) worker.execute { server.playbackFailed(session, stream) }
            }
        }
    }

    fun disableLanCompletely(errorDetail: String? = null) {
        enabled = false
        main.removeCallbacks(pairingExpiryRunnable)
        pairingWindow.closeWindow()
        unregisterLanNetworkCallback()
        activeLanNetworks.clear()
        teardownSocketAndNsdAsync()
        state = state.copy(
            enabled = false,
            listening = false,
            waitingForLan = false,
            port = 0,
            localIp = null,
            nsdRegistered = false,
            nsdServiceName = null,
            activeInvite = null,
            pendingPairing = null,
            connectedPeers = emptyList(),
            lastError = errorDetail
        )
        onActiveSessionCountChanged(0)
        onRemoteAudioActiveChanged(false)
        publish()
    }

    private fun bindServerAsync(network: Network, localIp: String) {
        val generation = ++bindingGeneration
        val window = PairingWindowController(onStateChanged = {
            main.post { if (generation == bindingGeneration) onPairingStateUpdated() }
        })
        pairingWindow = window
        bindingNetwork = network
        bindingIp = localIp
        worker.execute {
            if (generation != bindingGeneration) return@execute
            try {
                val id = identity ?: AndroidKeystoreIdentity.getOrCreate().also { identity = it }
                val created = SrapTlsServer(
                    identity = id,
                    appVersion = appVersion,
                    peers = peerStore,
                    pairingWindow = window,
                    bridge = operationBridge,
                    elapsedMs = android.os.SystemClock::elapsedRealtime,
                    onAliasChanged = { main.post { if (generation == bindingGeneration) publish() } },
                    bindAddress = InetAddress.getByName(localIp),
                    onActiveSessionsChanged = { _ ->
                        onAudioThread(Unit) {
                            if (generation == bindingGeneration) {
                                val connected = tlsServer?.activeAuthenticatedPeers().orEmpty()
                                state = state.copy(connectedPeers = connected, trustedPeers = peerStore.listPeers())
                                onActiveSessionCountChanged(connected.size)
                                publish()
                            }
                        }
                    }
                )
                created.start()
                main.post {
                    if (generation != bindingGeneration || closed) {
                        // This instance was never installed. Its sockets still need closing.
                        closeDetachedServer(created)
                    } else {
                        tlsServer = created
                        publishListeningEndpoint()
                    }
                }
            } catch (_: Exception) {
                main.post {
                    if (generation == bindingGeneration) {
                        state = state.copy(listening = false, lastError = "lan_bind_failed")
                        publish()
                    }
                }
            }
        }
    }

    private fun publishListeningEndpoint() {
        val server = tlsServer ?: return
        val ip = bindingIp ?: return
        val id = identity ?: return
        if (pairingRequested) {
            pairingRequested = false
            pairingWindow.openWindow(id.fingerprintSha256Upper, ip, server.localPort,
                ttlMs = PairingWindowController.DEFAULT_WINDOW_TTL_MS)
            main.removeCallbacks(pairingExpiryRunnable)
            main.postDelayed(pairingExpiryRunnable, PairingWindowController.DEFAULT_WINDOW_TTL_MS)
        }
        state = state.copy(enabled = true, listening = true, waitingForLan = false, port = server.localPort,
            localIp = ip, androidFingerprintSha256 = id.fingerprintSha256Upper,
            activeInvite = pairingWindow.currentInvite(), trustedPeers = peerStore.listPeers(), lastError = null)
        updateNsdAdvertisement()
        publish()
    }

    private fun closeDetachedServer(server: SrapTlsServer) {
        server.retire()
        if (worker.isShutdown) { server.close(); return }
        val reason = if (enabled) "lan_changed" else "server_stopping"
        worker.execute {
            try { server.disconnectAllSessions(reason) }
            finally { server.close() }
        }
    }

    private fun teardownSocketAndNsdAsync() {
        ++bindingGeneration
        bindingIp = null
        bindingNetwork = null
        pairingRequested = false
        unregisterNsd()
        nsdInstanceToken = null
        val srv = tlsServer
        tlsServer = null
        if (srv != null) closeDetachedServer(srv)
        streamOwnerSessionId?.let(::releasePlayback)
    }

    private fun onPairingStateUpdated() {
        if (closed) return
        val currentInvite = pairingWindow.currentInvite()
        if (currentInvite == null) {
            main.removeCallbacks(pairingExpiryRunnable)
        }
        state = state.copy(
            activeInvite = currentInvite,
            pendingPairing = pairingWindow.currentPendingRequest(),
            trustedPeers = peerStore.listPeers()
        )
        updateNsdAdvertisement()
        publish()
    }

    private fun onLanNetworkUpdated(network: Network, ipv4: String?, removed: Boolean) {
        if (closed || !enabled) return
        if (removed) {
            activeLanNetworks.remove(network)
        } else {
            activeLanNetworks[network] = ipv4
        }
        val chosen = activeLanNetworks.entries.firstOrNull { it.key == bindingNetwork && it.value != null }
            ?: activeLanNetworks.entries.firstOrNull { it.value != null }
        if (chosen?.key == bindingNetwork && chosen?.value == bindingIp) return
        val shouldPair = bindingNetwork == null && pairingRequested
        closePairingWindow()
        teardownSocketAndNsdAsync()
        state = state.copy(listening = false, waitingForLan = chosen == null, port = 0, localIp = null,
            nsdRegistered = false, nsdServiceName = null, activeInvite = null, pendingPairing = null,
            connectedPeers = emptyList())
        onActiveSessionCountChanged(0)
        onRemoteAudioActiveChanged(false)
        onLanAvailabilityChanged(chosen != null)
        publish()
        if (chosen != null) {
            pairingRequested = shouldPair
            bindServerAsync(chosen.key, requireNotNull(chosen.value))
        }
    }

    private fun registerLanNetworkCallbackIfNeeded(): Boolean {
        if (lanCallback != null) return true
        val cm = connectivityManager ?: return false
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            .build()
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
                if (lanCallback === this) onLanNetworkUpdated(network, extractIpv4FromLinkProperties(linkProperties), false)
            }
            override fun onLost(network: Network) {
                if (lanCallback === this) onLanNetworkUpdated(network, null, true)
            }
        }
        return runCatching {
            cm.registerNetworkCallback(request, callback, main)
            lanCallback = callback
            true
        }.getOrDefault(false)
    }

    private fun unregisterLanNetworkCallback() {
        val callback = lanCallback ?: return
        lanCallback = null
        runCatching { connectivityManager?.unregisterNetworkCallback(callback) }
    }

    private fun updateNsdAdvertisement() {
        val mgr = nsdManager ?: return
        val port = tlsServer?.localPort ?: 0
        if (!enabled || !state.listening || state.waitingForLan || port !in 1..65535) {
            unregisterNsd()
            return
        }
        val endpoint = Triple(bindingNetwork, port, pairingWindow.isWindowOpen())
        if (nsdListener != null && advertisedEndpoint == endpoint) return
        unregisterNsd()
        advertisedEndpoint = endpoint

        // Strictly minimal ephemeral NSD advertisement: ONLY major version, ephemeral token, and pair flag.
        val token = nsdInstanceToken ?: SrapCrypto.toHexLower(SrapCrypto.randomBytes(8)).also { nsdInstanceToken = it }
        val svcName = "sr-${token.take(8)}"
        val info = NsdServiceInfo().apply {
            serviceName = svcName
            serviceType = NSD_SERVICE_TYPE
            setPort(port)
            if (android.os.Build.VERSION.SDK_INT >= 33) network = bindingNetwork
            setAttribute("v", SrapCodec.MAJOR.toString())
            setAttribute("inst", token)
            setAttribute("pair", if (pairingWindow.isWindowOpen()) "1" else "0")
        }
        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo?) {
                main.post {
                    if (!closed && nsdListener === this) {
                        nsdRegistered = true
                        nsdServiceName = serviceInfo?.serviceName ?: svcName
                        state = state.copy(nsdRegistered = true, nsdServiceName = nsdServiceName)
                        publish()
                    }
                }
            }

            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo?, errorCode: Int) {
                main.post {
                    if (!closed && nsdListener === this) {
                        nsdRegistered = false
                        advertisedEndpoint = null
                        state = state.copy(nsdRegistered = false, lastError = "nsd_reg_failed:$errorCode")
                        publish()
                    }
                }
            }

            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo?) = Unit
            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo?, errorCode: Int) = Unit
        }
        nsdListener = listener
        runCatching { mgr.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener) }.onFailure {
            nsdListener = null
            advertisedEndpoint = null
            state = state.copy(nsdRegistered = false, nsdServiceName = null, lastError = "nsd_registration_failed")
        }
    }

    private fun unregisterNsd() {
        advertisedEndpoint = null
        val listener = nsdListener ?: return
        nsdListener = null
        nsdRegistered = false
        nsdServiceName = null
        runCatching { nsdManager?.unregisterService(listener) }
    }

    private fun extractIpv4FromLinkProperties(lp: LinkProperties?): String? {
        if (lp == null) return null
        for (la in lp.linkAddresses) {
            val addr = la.address
            if (addr is Inet4Address && !addr.isLoopbackAddress && addr.isSiteLocalAddress) {
                return addr.hostAddress
            }
        }
        return null
    }


    private fun publish() {
        if (!closed) {
            state = state.copy(aliasBinding = tlsServer?.aliasPrompt())
            onStateChanged(state)
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        disableLanCompletely()
        worker.shutdown()
    }

    private companion object {
        const val NSD_SERVICE_TYPE = "_sonicroute._tcp."
    }
}
