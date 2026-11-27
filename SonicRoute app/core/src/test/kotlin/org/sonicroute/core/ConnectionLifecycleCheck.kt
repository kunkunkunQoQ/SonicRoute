package org.sonicroute.core

fun main() {
    checkDefaultOffAndZeroIdleResources()
    checkBackgroundAndLockScreenPersistence()
    checkWifiToCellularToWifiRecovery()
    checkExplicitStopForbidsAutoRestore()
    checkProcessReclaimSafeDegradation()
    println("PASS ConnectionLifecycle 4-layer separation & power-gating checks")
}

private fun checkDefaultOffAndZeroIdleResources() {
    val m = ConnectionLifecycleMachine(persistedWasActiveBeforeKill = false)
    val s0 = m.snapshot()
    check(s0.phase == LanServicePhase.OFF)
    check(!s0.shouldRunForegroundService)
    check(!s0.shouldBindLanServer)
    check(!s0.shouldAdvertiseNsd)
    check(!s0.shouldHoldAudioObservers)
    check(!s0.shouldHoldPermissionListeners)

    // Opening UI attaches audio & permission observers, but LAN remains strictly OFF.
    val s1 = m.onUiAttached()
    check(s1.phase == LanServicePhase.OFF)
    check(!s1.shouldRunForegroundService)
    check(s1.shouldHoldAudioObservers)
    check(s1.shouldHoldPermissionListeners)

    // Leaving UI while LAN is OFF drops ALL observers back to zero.
    val s2 = m.onUiDetached()
    check(!s2.shouldHoldAudioObservers)
    check(!s2.shouldHoldPermissionListeners)
    check(!s2.shouldRunForegroundService)
}

private fun checkBackgroundAndLockScreenPersistence() {
    val m = ConnectionLifecycleMachine()
    m.onUiAttached()
    val s1 = m.onUserStartLan(openPairing = true, currentLanAvailable = true)
    check(s1.phase == LanServicePhase.LISTENING_PAIRING)
    check(s1.shouldRunForegroundService)
    check(s1.shouldBindLanServer && s1.shouldAdvertiseNsd)
    check(s1.pairingOpen)

    // Leaving Activity (HOME / app switch / screen lock) MUST close pairing window,
    // keep FGS + TLS server + NSD alive, and detach idle audio observers when 0 PCs connected.
    val s2 = m.onUiDetached()
    check(!s2.pairingOpen)
    check(s2.phase == LanServicePhase.LISTENING_IDLE)
    check(s2.shouldRunForegroundService)
    check(s2.shouldBindLanServer && s2.shouldAdvertiseNsd)
    check(!s2.shouldHoldAudioObservers) // Power win: zero audio observers while idle in background!
    check(s2.shouldHoldPermissionListeners)

    // Trusted PC connects over mTLS while screen is locked -> audio observers wake lazily!
    val s3 = m.onActiveSessionsChanged(1)
    check(s3.phase == LanServicePhase.CONNECTED)
    check(s3.shouldHoldAudioObservers)
    check(!s3.shouldIncludeMediaPlaybackFgsType)

    // PC starts owned audio playback -> FGS elevates to include mediaPlayback type.
    val s4 = m.onRemoteAudioActiveChanged(true)
    check(s4.shouldIncludeMediaPlaybackFgsType)

    // PC disconnects in background -> audio observers release immediately again!
    val s5 = m.onActiveSessionsChanged(0)
    check(s5.phase == LanServicePhase.LISTENING_IDLE)
    check(!s5.shouldHoldAudioObservers)
    check(!s5.shouldIncludeMediaPlaybackFgsType)
    check(s5.shouldRunForegroundService)
}

private fun checkWifiToCellularToWifiRecovery() {
    val m = ConnectionLifecycleMachine()
    m.onUiAttached()
    m.onUserStartLan(openPairing = false, currentLanAvailable = true)
    m.onUiDetached()
    m.onActiveSessionsChanged(1)
    m.onRemoteAudioActiveChanged(true)
    check(m.snapshot().phase == LanServicePhase.CONNECTED)

    // Leave home Wi-Fi -> Cellular: lanAvailable becomes false.
    // Must tear down sockets/NSD/sessions/audio while preserving user intent (WAITING_FOR_LAN).
    val away = m.onLanAvailabilityChanged(false)
    check(away.phase == LanServicePhase.WAITING_FOR_LAN)
    check(away.lanEnabled && away.shouldRunForegroundService)
    check(!away.shouldBindLanServer)
    check(!away.shouldAdvertiseNsd)
    check(!away.shouldHoldAudioObservers)
    check(away.activeSessionCount == 0 && !away.remoteAudioActive)

    // Return to home Wi-Fi: automatically re-binds TLS server & NSD without re-opening pairing!
    val back = m.onLanAvailabilityChanged(true)
    check(back.phase == LanServicePhase.LISTENING_IDLE)
    check(!back.pairingOpen)
    check(back.shouldBindLanServer && back.shouldAdvertiseNsd)
}

private fun checkExplicitStopForbidsAutoRestore() {
    val m = ConnectionLifecycleMachine()
    m.onUiAttached()
    m.onUserStartLan(openPairing = true, currentLanAvailable = true)
    m.onActiveSessionsChanged(1)
    m.onUiDetached()

    // User taps Stop (e.g. from notification action while in background).
    val stopped = m.onUserStopLan()
    check(stopped.phase == LanServicePhase.OFF)
    check(stopped.lastStopReason == LanStopReason.USER_STOPPED)
    check(!stopped.lanEnabled)
    check(!stopped.shouldRunForegroundService)
    check(!stopped.shouldBindLanServer)
    check(!stopped.shouldAdvertiseNsd)
    check(!stopped.shouldHoldAudioObservers)
    check(!stopped.shouldHoldPermissionListeners)

    // Network toggling back ON must NEVER auto-resume a user-stopped connection!
    val wifiBack = m.onLanAvailabilityChanged(true)
    check(wifiBack.phase == LanServicePhase.OFF)
    check(!wifiBack.shouldRunForegroundService)
    check(!wifiBack.shouldBindLanServer)
}

private fun checkProcessReclaimSafeDegradation() {
    // Process restarted after unexpected OS kill while LAN was previously enabled.
    // Must boot safely OFF (no zombie auto-start) and report PROCESS_RECLAIMED for clear UI recovery.
    val m = ConnectionLifecycleMachine(persistedWasActiveBeforeKill = true)
    val s0 = m.snapshot()
    check(s0.phase == LanServicePhase.OFF)
    check(s0.lastStopReason == LanStopReason.PROCESS_RECLAIMED)
    check(!s0.shouldRunForegroundService)
    check(!s0.shouldBindLanServer)

    // FGS denied by OS (e.g. background start restriction) degrades cleanly.
    m.onUiAttached()
    m.onUserStartLan(openPairing = false, currentLanAvailable = true)
    val denied = m.onFgsStartFailed()
    check(denied.phase == LanServicePhase.OFF)
    check(denied.lastStopReason == LanStopReason.FGS_START_DENIED)
    check(!denied.shouldRunForegroundService)
}
