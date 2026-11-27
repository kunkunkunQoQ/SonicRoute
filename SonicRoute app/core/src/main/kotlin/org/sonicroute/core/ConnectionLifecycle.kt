package org.sonicroute.core

enum class LanServicePhase {
    OFF,
    WAITING_FOR_LAN,
    LISTENING_IDLE,
    LISTENING_PAIRING,
    CONNECTED
}

enum class LanStopReason {
    NONE,
    USER_STOPPED,
    PROCESS_RECLAIMED,
    FGS_START_DENIED
}

/**
 * Pure snapshot separating UI, Network Service, Permission Backend, and Audio Session lifecycles.
 * Every background resource decision is derived deterministically from this snapshot.
 */
data class ConnectionLifecycleSnapshot(
    val uiAttached: Boolean = false,
    val lanEnabled: Boolean = false,
    val lanAvailable: Boolean = false,
    val pairingOpen: Boolean = false,
    val activeSessionCount: Int = 0,
    val remoteAudioActive: Boolean = false,
    val localUiAudioActive: Boolean = false,
    val lastStopReason: LanStopReason = LanStopReason.NONE
) {
    val phase: LanServicePhase
        get() = when {
            !lanEnabled -> LanServicePhase.OFF
            !lanAvailable -> LanServicePhase.WAITING_FOR_LAN
            activeSessionCount > 0 -> LanServicePhase.CONNECTED
            pairingOpen -> LanServicePhase.LISTENING_PAIRING
            else -> LanServicePhase.LISTENING_IDLE
        }

    /** Foreground service runs ONLY while the user explicitly enabled PC LAN connection. */
    val shouldRunForegroundService: Boolean get() = lanEnabled

    /** MediaPlayback FGS type is added only while actual audio playback is active under FGS. */
    val shouldIncludeMediaPlaybackFgsType: Boolean get() = lanEnabled && (remoteAudioActive || localUiAudioActive)

    /** Socket listener and NSD operate ONLY when LAN (Wi-Fi/Ethernet) is available; never on cellular alone. */
    val shouldBindLanServer: Boolean get() = lanEnabled && lanAvailable
    val shouldAdvertiseNsd: Boolean get() = lanEnabled && lanAvailable

    /** Power optimization: detach audio/volume/Bluetooth observers when UI is hidden AND no PC is connected. */
    val shouldHoldAudioObservers: Boolean get() = uiAttached || activeSessionCount > 0

    /** Permission listeners stay active while UI is visible or LAN service is enabled. */
    val shouldHoldPermissionListeners: Boolean get() = uiAttached || lanEnabled

    /** Unauthenticated pairing is strictly foreground-UI gated. */
    val canOpenPairing: Boolean get() = uiAttached && lanEnabled && lanAvailable
}

/**
 * Thread-safe state machine governing UI, Network/FGS, Permission, and Audio lifecycles.
 */
class ConnectionLifecycleMachine(persistedWasActiveBeforeKill: Boolean = false) {
    private val lock = Any()
    private var state = ConnectionLifecycleSnapshot(
        uiAttached = false,
        lanEnabled = false,
        lanAvailable = false,
        pairingOpen = false,
        activeSessionCount = 0,
        remoteAudioActive = false,
        localUiAudioActive = false,
        lastStopReason = if (persistedWasActiveBeforeKill) {
            LanStopReason.PROCESS_RECLAIMED
        } else {
            LanStopReason.NONE
        }
    )

    fun snapshot(): ConnectionLifecycleSnapshot = synchronized(lock) { state }

    fun onUiAttached(): ConnectionLifecycleSnapshot = update {
        copy(uiAttached = true)
    }

    /**
     * Leaving the Activity (HOME, app switch, screen lock) MUST close any unconfirmed pairing window
     * and stop UI-only local test audio, while preserving an explicitly enabled PC LAN connection
     * and active remote sessions.
     */
    fun onUiDetached(): ConnectionLifecycleSnapshot = update {
        copy(
            uiAttached = false,
            pairingOpen = false,
            localUiAudioActive = false
        )
    }

    fun onUserStartLan(openPairing: Boolean, currentLanAvailable: Boolean): ConnectionLifecycleSnapshot = update {
        val allowPairing = openPairing && uiAttached && currentLanAvailable
        copy(
            lanEnabled = true,
            lanAvailable = currentLanAvailable,
            pairingOpen = allowPairing,
            lastStopReason = LanStopReason.NONE
        )
    }

    fun onUserOpenPairing(): ConnectionLifecycleSnapshot = update {
        if (!uiAttached || !lanEnabled || !lanAvailable) this
        else copy(pairingOpen = true)
    }

    fun onUserClosePairing(): ConnectionLifecycleSnapshot = update {
        if (!pairingOpen) this else copy(pairingOpen = false)
    }

    /** Explicit user stop releases everything and forbids automatic restart. */
    fun onUserStopLan(): ConnectionLifecycleSnapshot = update {
        copy(
            lanEnabled = false,
            pairingOpen = false,
            activeSessionCount = 0,
            remoteAudioActive = false,
            lastStopReason = LanStopReason.USER_STOPPED
        )
    }

    fun onFgsStartFailed(): ConnectionLifecycleSnapshot = update {
        copy(
            lanEnabled = false,
            pairingOpen = false,
            activeSessionCount = 0,
            remoteAudioActive = false,
            lastStopReason = LanStopReason.FGS_START_DENIED
        )
    }

    /**
     * Leaving Wi-Fi (e.g. moving to cellular) tears down LAN sockets/NSD/sessions cleanly without
     * disabling user intent (`lanEnabled` stays true in WAITING_FOR_LAN). Returning to Wi-Fi restores
     * listening automatically for trusted peers without re-opening pairing.
     */
    fun onLanAvailabilityChanged(available: Boolean): ConnectionLifecycleSnapshot = update {
        if (lanAvailable == available) return@update this
        if (!available) {
            copy(
                lanAvailable = false,
                pairingOpen = false,
                activeSessionCount = 0,
                remoteAudioActive = false
            )
        } else {
            copy(lanAvailable = true)
        }
    }

    fun onActiveSessionsChanged(count: Int): ConnectionLifecycleSnapshot = update {
        val safe = count.coerceAtLeast(0)
        copy(
            activeSessionCount = safe,
            remoteAudioActive = if (safe == 0) false else remoteAudioActive
        )
    }

    fun onRemoteAudioActiveChanged(active: Boolean): ConnectionLifecycleSnapshot = update {
        copy(remoteAudioActive = active && activeSessionCount > 0)
    }

    fun onLocalUiAudioActiveChanged(active: Boolean): ConnectionLifecycleSnapshot = update {
        copy(localUiAudioActive = active && uiAttached)
    }

    private inline fun update(block: ConnectionLifecycleSnapshot.() -> ConnectionLifecycleSnapshot): ConnectionLifecycleSnapshot {
        return synchronized(lock) {
            state = state.block()
            state
        }
    }
}
