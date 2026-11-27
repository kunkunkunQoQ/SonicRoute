package org.sonicroute.platform

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import org.sonicroute.core.Backend
import org.sonicroute.core.Capability
import org.sonicroute.core.CapabilityEntry
import org.sonicroute.core.CapabilityStatus
import org.sonicroute.core.ConnectionLifecycleMachine
import org.sonicroute.core.ConnectionLifecycleSnapshot
import org.sonicroute.core.FeasibilityReport
import org.sonicroute.core.JsonBool
import org.sonicroute.core.JsonObject
import org.sonicroute.core.LanStopReason
import org.sonicroute.core.LocalVolumeRule
import org.sonicroute.core.MediaPlayerSnapshot
import org.sonicroute.core.OutputEvent
import org.sonicroute.core.ProtocolError
import org.sonicroute.core.OperationResult
import org.sonicroute.core.OwnedPlaybackState
import org.sonicroute.core.PermissionSnapshot
import org.sonicroute.core.StrictJson
import org.sonicroute.core.SystemVolumeSnapshot
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.CopyOnWriteArraySet

data class SonicRouteRuntimeSnapshot(
    val permissions: PermissionSnapshot,
    val audio: LocalAudioState,
    val lan: LanServerState,
    val lifecycle: ConnectionLifecycleSnapshot,
    val foregroundServiceRunning: Boolean,
    val feasibilityReport: FeasibilityReport?,
    val probeInProgress: Boolean,
    val volumeRules: List<LocalVolumeRule>,
    val media: LocalMediaState,
    val publishedMediaEnabled: Boolean,
    val clipboardSharingEnabled: Boolean,
    val files: LocalFileSharing,
    val playbackSharing: SharedPlaybackState?,
    val screenSharing: SharedScreenState?,
    val nativeAppRoute: NativeAppRoutingState,
    val nativeRouteRules: NativeRouteRuleState
)

/**
 * Process-scoped coordinator cleanly separating 4 independent lifecycles:
 * 1. UI lifecycle (MainActivity attach/detach)
 * 2. Network & Foreground Service lifecycle (explicit user start/stop, LAN Wi-Fi recovery)
 * 3. Privileged Backend lifecycle (Shizuku/Direct Root observation & safe degradation)
 * 4. Audio/Media Session lifecycle (power-gated observers, owned-track playback)
 *
 * Holds zero Activity references, zero wake locks, and zero idle polling timers.
 */
class SonicRouteRuntime private constructor(context: Context) {

    private val appContext = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private val stateFile = File(appContext.filesDir, "lan_runtime_state.json")
    private val listeners = CopyOnWriteArraySet<(SonicRouteRuntimeSnapshot) -> Unit>()
    private val finishRecreation = Runnable { if (listeners.isEmpty()) finishUiDetach() }
    private val initialWords = AppAppearance.forStrings(appContext)
    private var localeTags = initialWords.resources.configuration.locales.toLanguageTags()

    private val lifecycleMachine = ConnectionLifecycleMachine(
        persistedWasActiveBeforeKill = consumePersistedActiveFlag()
    )

    private var permissionSession: PermissionSession? = null
    @Volatile private var lastPermissionSnapshot: PermissionSnapshot = PermissionSession.initialStandardSnapshot()
    private var lastLocalCapabilities: Map<Capability, CapabilityEntry> = emptyMap()
    private var lastKnownGeneration: Long = -1L

    private var lastAudioState: LocalAudioState = LocalAudioState()
    private var lastLanState: LanServerState = LanServerState()
    private var lastFeasibilityReport: FeasibilityReport? = null
    private var probeInProgress: Boolean = false
    @Volatile private var fgsRunning: Boolean = false
    private var lastFgsMediaFlag: Boolean = false
    private var reconciling: Boolean = false
    private var audioPageVisible = true
    private var diagnosticsVisible = false
    private var appPickerOpen = false
    private val sharingSettings = appContext.getSharedPreferences("media_sharing", Context.MODE_PRIVATE)
    private val keyguard = appContext.getSystemService(android.app.KeyguardManager::class.java)
    private var clipboardWindow: java.lang.ref.WeakReference<android.view.View>? = null
    private val sharedClipboard = LocalSharedClipboard(appContext, ::hasFocusedAppWindow)
    private val sharedCapture = SharedPlaybackCapture(appContext) {
        onLocalCapabilitiesObserved(lastLocalCapabilities)
        publish()
    }
    private val sharedScreen = SharedScreenProjection(appContext) {
        onLocalCapabilitiesObserved(lastLocalCapabilities)
        publish()
    }
    private val nativeAppRouting = NativeAppRouting(appContext, ::onNativeAppRoutingChanged)
    private val nativeRouteRules = NativeAppRoutingRules(appContext, nativeAppRouting, { lastPermissionSnapshot }, ::onNativeRulesChanged)
    private var clipboardWindowFocused = false
    private var clipboardSharingEnabled = sharingSettings.getBoolean("foreground_clipboard", false)
    var publishedMediaEnabled: Boolean = sharingSettings.getBoolean("paired_computers", false)
        private set
    private val mediaSessions: LocalMediaSessions = LocalMediaSessions(appContext, ::onMediaChanged)
    private val files = AndroidSharedFiles(appContext) {
        onLocalCapabilitiesObserved(lastLocalCapabilities)
        publish()
    }

    private val audioController: LocalAudioController = LocalAudioController(
        context = appContext,
        speakerLabel = initialWords.getString(R.string.local_audio_speaker),
        currentLabel = initialWords.getString(R.string.local_audio_follow),
        onStateChanged = ::onAudioStateChanged,
        onLocalCapabilitiesObserved = ::onLocalCapabilitiesObserved,
        onOutputKindsObserved = { kinds -> automation.onOutputs(kinds); nativeRouteRules.outputsChanged() }
    )

    private val automation = LocalVolumeAutomation(appContext, audioController::applyRuleVolume, ::onRulesChanged)

    private val lanController = LanServerController(
        context = appContext,
        appVersion = resolveAppVersion(),
        permissionSnapshotProvider = { lastPermissionSnapshot },
        audioController = audioController,
        onStateChanged = ::onLanStateChanged,
        onLanAvailabilityChanged = ::onLanAvailabilityChanged,
        onActiveSessionCountChanged = ::onActiveSessionCountChanged,
        onRemoteAudioActiveChanged = ::onRemoteAudioActiveChanged,
        readSharedMedia = ::readSharedMedia,
        readSharedMediaPackageState = ::readSharedMediaPackageState,
        sharedClipboard = sharedClipboard,
        sharedCapture = sharedCapture,
        sharedScreen = sharedScreen,
        fileStore = files,
        controlSharedMedia = ::controlSharedMedia
    )

    private var feasibilityRunner: PrivilegedFeasibilityRunner? = null

    init {
        lanController.initIdentityAsync()
        files.restore()
        audioController.setAutomationMonitoring(automation.active)
    }

    fun isForegroundServiceRunning(): Boolean = fgsRunning

    fun captureSessionForPeer(peer: String): String? = lanController.uniqueSessionForPeer(peer)
    fun captureSource(app: String, uid: Int) = sharedCapture.source(app, uid)
    fun sharePlayback(source: PlaybackCaptureSource, peer: String, session: String, resultCode: Int, data: android.content.Intent) =
        lanController.uniqueSessionForPeer(peer) == session && lanController.hasTrustedSession(session) &&
            sharedCapture.begin(source, peer, session, resultCode, data)
    fun stopSharingPlayback() = sharedCapture.stop()
    fun shareScreen(session: String, peer: String, result: Int, data: android.content.Intent, width: Int, height: Int, dpi: Int) =
        lanController.uniqueSessionForPeer(peer) == session && lanController.hasTrustedSession(session) &&
            sharedScreen.begin(session, peer, result, data, width, height, dpi)
    fun stopSharingScreen() = sharedScreen.stop()

    internal fun onProjectionServiceStopped(owner: Any) {
        if (owner === sharedScreen) sharedScreen.stop()
        if (owner === sharedCapture) sharedCapture.stop()
    }

    fun currentSnapshot(): SonicRouteRuntimeSnapshot = SonicRouteRuntimeSnapshot(
        permissions = lastPermissionSnapshot,
        audio = lastAudioState,
        lan = lastLanState,
        lifecycle = lifecycleMachine.snapshot(),
        foregroundServiceRunning = fgsRunning,
        feasibilityReport = lastFeasibilityReport,
        probeInProgress = probeInProgress,
        volumeRules = automation.snapshot,
        media = mediaSessions.state,
        publishedMediaEnabled = publishedMediaEnabled,
        clipboardSharingEnabled = clipboardSharingEnabled,
        files = files.state,
        playbackSharing = sharedCapture.state,
        screenSharing = sharedScreen.state,
        nativeAppRoute = nativeAppRouting.state,
        nativeRouteRules = nativeRouteRules.state
    )

    // --- 1. UI Lifecycle ---

    fun attachUi(listener: (SonicRouteRuntimeSnapshot) -> Unit) {
        main.removeCallbacks(finishRecreation)
        listeners += listener
        lifecycleMachine.onUiAttached()
        files.setVisible(true)
        reconcileResources()
        listener(currentSnapshot())
    }

    fun detachUi(listener: (SonicRouteRuntimeSnapshot) -> Unit, changingConfiguration: Boolean = false) {
        listeners -= listener
        if (listeners.isNotEmpty()) return
        main.removeCallbacks(finishRecreation)
        if (changingConfiguration) {
            files.setVisible(false, keepOffer = true)
            main.postDelayed(finishRecreation, 5000)
        } else finishUiDetach()
    }

    private fun finishUiDetach() {

        // Leaving Activity: cancel UI-only feasibility probes, close any unconfirmed pairing window,
        // and stop local-UI-only test playback, while keeping an enabled PC LAN connection alive!
        probeInProgress = false
        feasibilityRunner?.close()
        feasibilityRunner = null

        lifecycleMachine.onUiDetached()
        appPickerOpen = false
        files.setVisible(false)
        clipboardWindowFocused = false
        clipboardWindow = null
        updateClipboardGate()
        lanController.closePairingWindow()
        lanController.cancelAliasBinding()
        mediaSessions.cancelCommands(shared = false)
        audioController.saveLocalPreferences()
        audioController.stopLocalTestTone()
        reconcileResources()
    }

    fun setClipboardSharingEnabled(value: Boolean) {
        if (!lifecycleMachine.snapshot().uiAttached || clipboardSharingEnabled == value) return
        clipboardSharingEnabled = value
        sharingSettings.edit().putBoolean("foreground_clipboard", value).apply()
        updateClipboardGate()
        publish()
    }

    fun setClipboardWindowFocused(value: Boolean, view: android.view.View? = null) {
        clipboardWindowFocused = value
        clipboardWindow = if (value && view != null) java.lang.ref.WeakReference(view) else null
        updateClipboardGate()
    }

    private fun hasFocusedAppWindow(): Boolean = clipboardWindow?.get()?.let {
        it.isAttachedToWindow && it.hasWindowFocus() && it.isShown && it.windowVisibility == android.view.View.VISIBLE &&
            keyguard?.isDeviceLocked == false
    } == true

    private fun updateClipboardGate() {
        val lc = lifecycleMachine.snapshot()
        if (sharedClipboard.setAvailable(clipboardSharingEnabled && clipboardWindowFocused && lc.uiAttached && lc.activeSessionCount > 0 && hasFocusedAppWindow())) {
            onLocalCapabilitiesObserved(lastLocalCapabilities)
        }
    }

    fun refreshForeground() {
        files.restore()
        permissionSession?.refresh()
        audioController.refreshAll()
        mediaSessions.refresh()
    }

    fun selectSharedTree(uri: android.net.Uri, flags: Int) = files.select(uri, flags)
    fun revokeSharedTree() = files.clear()
    fun decideFileOffer(id: String, allow: Boolean) = files.decide(id, allow && hasFocusedAppWindow())

    private var devicesPageVisible = false
    fun setVisibleAudioPage(audio: Boolean, diagnostics: Boolean, devices: Boolean = false) {
        if (audioPageVisible == audio && diagnosticsVisible == diagnostics && devicesPageVisible == devices) return
        if (devicesPageVisible && !devices) {
            audioController.saveLocalPreferences()
            audioController.stopLocalTestTone()
        }
        if (audioPageVisible && !audio) mediaSessions.cancelCommands(shared = false)
        audioPageVisible = audio
        devicesPageVisible = devices
        diagnosticsVisible = diagnostics
        reconcileResources()
        if (diagnostics) audioController.retryCatalogIfFailed()
    }

    fun setAppPickerOpen(open: Boolean) {
        appPickerOpen = open && lifecycleMachine.snapshot().uiAttached
        reconcileResources()
        if (appPickerOpen) audioController.retryCatalogIfFailed()
    }

    fun retryAppCatalogIfFailed() {
        if (lifecycleMachine.snapshot().uiAttached) audioController.retryCatalogIfFailed()
    }

    fun setPublishedMediaEnabled(enabled: Boolean) {
        if (publishedMediaEnabled == enabled || !lifecycleMachine.snapshot().uiAttached) return
        publishedMediaEnabled = enabled
        sharingSettings.edit().putBoolean("paired_computers", enabled).apply()
        if (!enabled) mediaSessions.cancelCommands(shared = true)
        reconcileResources()
        mediaSessions.refresh()
        onMediaChanged()
    }

    fun readSharedMedia(): OperationResult<List<MediaPlayerSnapshot>> {
        if (!publishedMediaEnabled || lifecycleMachine.snapshot().activeSessionCount == 0) {
            return OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED, "media_sharing_disabled")
        }
        return mediaSessions.read()
    }

    private fun readSharedMediaPackageState(packageName: String): OperationResult<Boolean> {
        if (!publishedMediaEnabled || lifecycleMachine.snapshot().activeSessionCount == 0) {
            return OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED, "media_sharing_disabled")
        }
        return mediaSessions.readPackageState(packageName)
    }

    fun controlSharedMedia(sessionKey: String, action: String, positionMs: Long?,
                           completed: (OperationResult<MediaPlayerSnapshot>) -> Unit): AutoCloseable {
        if (!publishedMediaEnabled || lifecycleMachine.snapshot().activeSessionCount == 0) {
            completed(OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED, "media_sharing_disabled"))
            return AutoCloseable {}
        }
        return mediaSessions.control(sessionKey, action, positionMs, shared = true, completed)
    }

    fun controlLocalMedia(key: String, action: String, completed: (OperationResult<MediaPlayerSnapshot>) -> Unit): AutoCloseable {
        if (!audioPageVisible || !lifecycleMachine.snapshot().uiAttached) {
            completed(OperationResult.Failure(ProtocolError.CANCELED))
            return AutoCloseable {}
        }
        return mediaSessions.control(key, action, null, shared = false, completed)
    }

    private fun mediaCapability(): CapabilityEntry {
        val media = mediaSessions.state
        val reason = when {
            !publishedMediaEnabled -> "local_media_sharing_disabled"
            !media.accessGranted -> "notification_listener_permission"
            media.unavailable -> "media_sessions_unavailable"
            else -> null
        }
        val status = when (reason) {
            null -> CapabilityStatus.AVAILABLE
            "media_sessions_unavailable" -> CapabilityStatus.UNAVAILABLE
            else -> CapabilityStatus.NEEDS_AUTHORIZATION
        }
        return CapabilityEntry(status, reason = reason, scope = "active_media", limits = mapOf("packageState" to "1"))
    }

    private fun onMediaChanged() {
        onLocalCapabilitiesObserved(lastLocalCapabilities)
        if (publishedMediaEnabled && lifecycleMachine.snapshot().activeSessionCount > 0) lanController.onMediaSessionsChanged()
        publish()
    }

    // --- 2. Network & Foreground Service Lifecycle ---

    fun startLanByUser(openPairing: Boolean) {
        val lanUp = lanController.probeLanAvailableNow()
        lifecycleMachine.onUserStartLan(openPairing = openPairing, currentLanAvailable = lanUp)
        writePersistedActiveFlag(active = true, reason = "user_started")

        // Start Android Foreground Service while app is in foreground (fully Android 14-16 compliant).
        val started = SonicRouteConnectionService.startOrUpdate(appContext)
        if (!started) {
            onForegroundServiceFailed("fgs_start_blocked")
            return
        }
        lanController.enableLan(openPairing = openPairing)
        reconcileResources()
        publish()
    }

    fun openPairingWindow() {
        val snap = lifecycleMachine.snapshot()
        if (!snap.uiAttached) return
        if (!snap.lanEnabled) {
            startLanByUser(openPairing = true)
            return
        }
        lifecycleMachine.onUserOpenPairing()
        lanController.openPairingWindow()
        refreshNotificationIfRunning()
        publish()
    }

    fun closePairingWindow() {
        lifecycleMachine.onUserClosePairing()
        lanController.closePairingWindow()
        refreshNotificationIfRunning()
        publish()
    }

    fun decidePendingPairing(approve: Boolean) {
        lanController.decidePendingPairing(approve)
    }

    fun decideAliasBinding(confirmationId: String, approve: Boolean) {
        if (lifecycleMachine.snapshot().uiAttached) lanController.decideAliasBinding(confirmationId, approve)
    }

    fun revokePeer(fingerprintSha256: String) {
        lanController.revokePeer(fingerprintSha256)
    }

    fun stopLanByUser() {
        sharedScreen.stop()
        sharedCapture.stop()
        writePersistedActiveFlag(active = false, reason = "user_stopped")
        lifecycleMachine.onUserStopLan()
        lanController.disableLanCompletely()
        SonicRouteConnectionService.stop(appContext)
        fgsRunning = false
        lastFgsMediaFlag = false
        reconcileResources()
        publish()
    }

    internal fun onForegroundServiceActive(active: Boolean) {
        main.post {
            fgsRunning = active
            publish()
        }
    }

    internal fun onForegroundServiceFailed(reason: String) {
        main.post {
            fgsRunning = false
            writePersistedActiveFlag(active = false, reason = reason)
            lifecycleMachine.onFgsStartFailed()
            lanController.disableLanCompletely(errorDetail = reason)
            reconcileResources()
            publish()
        }
    }

    // --- 3. Privileged Backend Lifecycle ---

    fun selectBackend(backend: Backend) {
        ensurePermissionSessionStarted()
        permissionSession?.select(backend)
    }

    fun runFeasibilityProbe() {
        val snap = lastPermissionSnapshot
        if (feasibilityRunner == null) {
            feasibilityRunner = PrivilegedFeasibilityRunner(appContext) { report, capUpdates ->
                probeInProgress = false
                val current = permissionSession?.currentSnapshot() ?: lastPermissionSnapshot
                if (current.generation == report.generation) {
                    lastFeasibilityReport = report
                    // The launcher directory becomes available only after its current snapshot is ready.
                    permissionSession?.updateCapabilities(report.generation, capUpdates - Capability.APP_CATALOG)
                    publish()
                }
            }
        }
        probeInProgress = true
        publish()
        feasibilityRunner?.runProbe(
            snap,
            audioController.currentState(),
            audioController.activeSelfAudioSessionId()
        )
    }

    // --- 4. Audio & Device Commands ---

    fun nativeAppOutputs(): List<NativeAppOutput> =
        if (audioPageVisible && lifecycleMachine.snapshot().uiAttached) nativeAppRouting.physicalOutputs() else emptyList()

    fun routeLocalApp(packageName: String, selectedUid: Int, output: NativeAppOutput): Boolean {
        if (!audioPageVisible || !lifecycleMachine.snapshot().uiAttached || nativeAppRouting.state.active) return false
        val app = lastAudioState.appCatalog.entries.singleOrNull {
            !it.isSelf && it.packageName == packageName && it.uid == selectedUid
        } ?: return false
        nativeRouteRules.disarm(false)
        nativeAppRouting.apply(app, output, lastPermissionSnapshot)
        return true
    }

    fun restoreLocalAppRoute() {
        if (audioPageVisible && lifecycleMachine.snapshot().uiAttached) {
            nativeRouteRules.disarm(false)
            nativeAppRouting.restore()
        }
    }

    fun saveNativeRouteRule(pkg: String, uid: Int, output: NativeAppOutput, label: String): Boolean {
        if (!audioPageVisible || !lifecycleMachine.snapshot().uiAttached) return false
        val app = lastAudioState.appCatalog.entries.singleOrNull { !it.isSelf && it.packageName == pkg && it.uid == uid } ?: return false
        return nativeRouteRules.save(app, output, label)
    }

    fun useNativeRouteRule(pkg: String): Boolean {
        if (!audioPageVisible || !lifecycleMachine.snapshot().uiAttached) return false
        if (nativeRouteRules.state.armed == pkg) { nativeRouteRules.disarm(true); return true }
        return nativeRouteRules.arm(pkg)
    }

    fun removeNativeRouteRule(pkg: String) {
        if (audioPageVisible && lifecycleMachine.snapshot().uiAttached) nativeRouteRules.remove(pkg)
    }

    private fun onNativeAppRoutingChanged() {
        nativeRouteRules.routeChanged()
        reconcileResources()
        publish()
    }

    private fun onNativeRulesChanged() {
        audioController.setAutomationMonitoring(automation.active || nativeRouteRules.active)
        reconcileResources()
        publish()
    }

    fun selectOutputTarget(targetKey: String) = audioController.selectLocalOutput(targetKey)

    fun toggleLocalTestTone() {
        val state = audioController.currentState().localPlayback.state
        if (state == OwnedPlaybackState.PLAYING || state == OwnedPlaybackState.STARTING) {
            audioController.stopLocalTestTone()
        } else {
            audioController.startLocalTestTone()
        }
    }

    fun toggleOwnedTrackMute() {
        audioController.setLocalTrackMuted(!audioController.currentState().localPlayback.trackMuted)
    }

    fun setOwnedTrackGainPercent(percent: Int) = audioController.setLocalTrackGain(percent)
    fun saveLocalPlaybackSettings() = audioController.saveLocalPreferences()
    fun setOwnedFocusEnabled(enabled: Boolean) = audioController.setOwnedFocusEnabled(enabled)

    fun saveVolumeRule(rule: LocalVolumeRule) = automation.save(rule)
    fun removeVolumeRule(event: OutputEvent) = automation.remove(event)

    private fun onRulesChanged() {
        audioController.setAutomationMonitoring(automation.active || nativeRouteRules.active)
        publish()
    }

    fun setSystemMediaVolumePercent(percent: Int): OperationResult<SystemVolumeSnapshot> =
        audioController.setSystemMediaVolumePercent(percent)

    fun buildBluetoothSettingsIntent() = audioController.buildBluetoothSettingsIntent()

    // --- Internal Reconciliation & Callbacks ---

    private fun reconcileResources() {
        if (reconciling) return
        reconciling = true
        try {
            val lc = lifecycleMachine.snapshot()

            // Permission backend listeners: active ONLY while UI is visible OR LAN service is enabled.
            if (lc.shouldHoldPermissionListeners || nativeAppRouting.state.active || nativeRouteRules.active) {
                ensurePermissionSessionStarted()
            } else {
                teardownPermissionSession()
            }

            // Audio / volume / Bluetooth observers: active ONLY while UI is visible OR a trusted PC is connected.
            audioController.setObserversActive(lc.activeSessionCount > 0 || lc.uiAttached && (audioPageVisible || devicesPageVisible || diagnosticsVisible))
            audioController.setCatalogNeeded(org.sonicroute.core.LocalAppCatalog.isNeeded(
                lc.activeSessionCount, lc.uiAttached, appPickerOpen, diagnosticsVisible))
            mediaSessions.setNeeded(lc.uiAttached && audioPageVisible || lc.activeSessionCount > 0 && publishedMediaEnabled)
            updateClipboardGate()

            // Update FGS notification & mediaPlayback FGS type in-place without background startForegroundService.
            if (lc.shouldRunForegroundService && fgsRunning) {
                val needMedia = lc.shouldIncludeMediaPlaybackFgsType
                if (needMedia != lastFgsMediaFlag) {
                    lastFgsMediaFlag = needMedia
                    SonicRouteConnectionService.updateForegroundTypeIfRunning(appContext)
                } else {
                    SonicRouteConnectionService.updateIfRunning(appContext)
                }
            }
        } finally {
            reconciling = false
        }
    }

    private fun ensurePermissionSessionStarted() {
        if (permissionSession != null) return
        val session = PermissionSession(::onPermissionsChanged)
        permissionSession = session
        lastKnownGeneration = -1L
        session.start()
    }

    private fun teardownPermissionSession() {
        val existing = permissionSession ?: return
        permissionSession = null
        existing.close()
        lastPermissionSnapshot = PermissionSession.initialStandardSnapshot()
        lastKnownGeneration = -1L
        lastFeasibilityReport = null
        probeInProgress = false
    }

    private fun onPermissionsChanged(snapshot: PermissionSnapshot) {
        val genChanged = snapshot.generation != lastKnownGeneration
        lastPermissionSnapshot = snapshot
        nativeRouteRules.backendChanged(snapshot)
        nativeAppRouting.backendChanged(snapshot)
        if (genChanged) {
            lastKnownGeneration = snapshot.generation
            probeInProgress = false
            feasibilityRunner?.cancelActive()
            lastFeasibilityReport = null
            if (lastLocalCapabilities.isNotEmpty() &&
                permissionSession?.updateCapabilities(snapshot.generation, lastLocalCapabilities) == true
            ) {
                return
            }
        }
        lanController.onPermissionSnapshotChanged(snapshot)
        publish()
    }

    private fun onLocalCapabilitiesObserved(updates: Map<Capability, CapabilityEntry>) {
        val tree = files.state
        val filesStatus = when {
            tree.folder != null -> CapabilityStatus.AVAILABLE
            tree.hasSavedSelection -> CapabilityStatus.UNAVAILABLE
            else -> CapabilityStatus.NEEDS_AUTHORIZATION
        }
        val fileCapability = CapabilityEntry(
            filesStatus, reason = when (filesStatus) {
                CapabilityStatus.NEEDS_AUTHORIZATION -> "select_shared_folder"
                CapabilityStatus.UNAVAILABLE -> "shared_folder_unavailable"
                else -> null
            }, scope = "saf_tree",
            limits = mapOf("grant" to files.grant.toString(), "write" to tree.writable.toString(),
                "chunk" to "32768", "maxFile" to "536870912"))
        lastLocalCapabilities = updates + (Capability.CAPTURE_APP to sharedCapture.capability()) +
            (Capability.MIRROR_DISPLAY to sharedScreen.capability()) +
            (Capability.FILE_TRANSFER to fileCapability) + (Capability.MEDIA_CONTROL to mediaCapability()) +
            (Capability.CLIPBOARD_SYNC to CapabilityEntry(
                if (sharedClipboard.available) CapabilityStatus.AVAILABLE else CapabilityStatus.NEEDS_AUTHORIZATION,
                reason = if (sharedClipboard.available) null else "focused_clipboard_required", scope = "focused_text"))
        val current = permissionSession?.currentSnapshot() ?: return
        permissionSession?.updateCapabilities(current.generation, lastLocalCapabilities)
    }

    private fun onAudioStateChanged(state: LocalAudioState) {
        val remotePlaybackFailed = state.playback.state == OwnedPlaybackState.ERROR &&
            lastAudioState.playback.state != OwnedPlaybackState.ERROR
        lastAudioState = state
        val playing = state.localPlayback.state == OwnedPlaybackState.PLAYING ||
            state.localPlayback.state == OwnedPlaybackState.STARTING
        val lc = lifecycleMachine.snapshot()
        if (lc.uiAttached && lc.localUiAudioActive != playing) {
            lifecycleMachine.onLocalUiAudioActiveChanged(playing)
            reconcileResources()
        }
        if (remotePlaybackFailed) lanController.onRemotePlaybackFailed()
        publish()
    }

    private fun onLanStateChanged(state: LanServerState) {
        val previous = lastLanState
        lastLanState = state
        sharedCapture.checkPeers(state.connectedPeers)
        sharedScreen.checkSession(lanController::hasTrustedSession)
        if (state.activeInvite != null && !lifecycleMachine.snapshot().pairingOpen) lifecycleMachine.onUserOpenPairing()
        else if (state.activeInvite == null && lifecycleMachine.snapshot().pairingOpen) lifecycleMachine.onUserClosePairing()
        if (previous.listening != state.listening || previous.waitingForLan != state.waitingForLan ||
            previous.connectedPeers != state.connectedPeers || (previous.activeInvite == null) != (state.activeInvite == null)) {
            refreshNotificationIfRunning()
        }
        publish()
    }

    private fun onLanAvailabilityChanged(available: Boolean) {
        lifecycleMachine.onLanAvailabilityChanged(available)
        reconcileResources()
        refreshNotificationIfRunning()
        publish()
    }

    private fun onActiveSessionCountChanged(count: Int) {
        lifecycleMachine.onActiveSessionsChanged(count)
        if (count == 0) mediaSessions.cancelCommands(shared = true)
        reconcileResources()
        refreshNotificationIfRunning()
        publish()
    }

    private fun onRemoteAudioActiveChanged(active: Boolean) {
        lifecycleMachine.onRemoteAudioActiveChanged(active)
        reconcileResources()
        refreshNotificationIfRunning()
        publish()
    }

    private fun refreshNotificationIfRunning() {
        if (fgsRunning && lifecycleMachine.snapshot().shouldRunForegroundService) {
            SonicRouteConnectionService.updateIfRunning(appContext)
        }
    }

    private fun publish() {
        val snap = currentSnapshot()
        for (listener in listeners) {
            listener(snap)
        }
    }

    private fun consumePersistedActiveFlag(): Boolean {
        val wasActive = runCatching {
            if (!stateFile.isFile) return@runCatching false
            val root = StrictJson.parseUtf8(stateFile.readBytes()) as? JsonObject ?: return@runCatching false
            (root["active"] as? JsonBool)?.value == true
        }.getOrDefault(false)
        if (wasActive) {
            writePersistedActiveFlag(active = false, reason = "process_reclaimed")
        }
        return wasActive
    }

    private fun writePersistedActiveFlag(active: Boolean, reason: String) {
        runCatching {
            stateFile.parentFile?.mkdirs()
            val payload = StrictJson.encodeUtf8(
                StrictJson.obj(
                    "active" to StrictJson.bool(active),
                    "reason" to StrictJson.str(reason)
                )
            )
            val tmp = File(stateFile.parentFile, stateFile.name + ".tmp")
            FileOutputStream(tmp).use { fos ->
                fos.write(payload)
                fos.flush()
                runCatching { fos.fd.sync() }
            }
            if (!tmp.renameTo(stateFile)) {
                stateFile.writeBytes(payload)
                tmp.delete()
            }
        }
    }

    private fun resolveAppVersion(): String = runCatching {
        if (Build.VERSION.SDK_INT >= 33) {
            appContext.packageManager.getPackageInfo(
                appContext.packageName,
                PackageManager.PackageInfoFlags.of(0)
            ).versionName
        } else {
            @Suppress("DEPRECATION")
            appContext.packageManager.getPackageInfo(appContext.packageName, 0).versionName
        }
    }.getOrNull() ?: "0.1.0"

    fun refreshAppearance() {
        val words = AppAppearance.forStrings(appContext)
        val tags = words.resources.configuration.locales.toLanguageTags()
        if (localeTags == tags) return
        localeTags = tags
        audioController.updateOutputLabels(words.getString(R.string.local_audio_speaker), words.getString(R.string.local_audio_follow))
        SonicRouteConnectionService.refreshLanguageIfRunning(appContext)
    }

    companion object {
        @Volatile private var instance: SonicRouteRuntime? = null

        fun getInstance(context: Context): SonicRouteRuntime {
            return instance ?: synchronized(this) {
                instance ?: SonicRouteRuntime(context.applicationContext).also { instance = it }
            }
        }
    }
}
