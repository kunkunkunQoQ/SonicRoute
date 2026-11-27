package org.sonicroute.core

fun main() {
    checkPermissionState()
    checkCapabilityLeasesAndRevisions()
    checkAudioSinkNormalizer()
    checkSystemVolumeMath()
    checkLauncherCatalog()
    println("All core checks passed")
}

private fun checkLauncherCatalog() {
    val cache = LocalAppCatalog()
    check(!LocalAppCatalog.isNeeded(0, true, false, false))
    check(!LocalAppCatalog.isNeeded(0, false, true, true))
    check(LocalAppCatalog.isNeeded(0, true, true, false))
    check(LocalAppCatalog.isNeeded(0, true, false, true))
    check(LocalAppCatalog.isNeeded(1, false, false, false))
    var scans = 0
    val loads = mutableListOf<String>()
    fun app(pkg: String, uid: Int, label: String, apk: String = pkg) =
        LocalAppCatalog.Source(pkg, uid, apk, "Launcher", "label") { loads += pkg; label }
    var apps = listOf(app("self", 101, "One"), app("two", 102, "Two"))
    fun read(language: String = "en", changes: LocalAppCatalog.Changes? = null, force: Boolean = false,
             changed: Set<String> = emptySet(), failed: Boolean = false, cancel: () -> Boolean = { false }) =
        cache.read(language, "self", changes, failed, force, changed, cancel) { scans++; apps.asSequence() }
    val first = checkNotNull(read())
    check(scans == 1 && loads == listOf("self", "two") && first.entries.single { it.isSelf }.uid == 101)
    repeat(12) { check(read() === first) }
    check(scans == 1 && loads.size == 2)
    loads.clear()
    apps = listOf(app("self", 101, "One"), app("two", 103, "Replacement"))
    val updated = checkNotNull(read(changes = LocalAppCatalog.Changes(4, setOf("two"))))
    check(cache.sequence == 4 && loads == listOf("two") && updated.entries.single { it.packageName == "two" }.uid == 103)
    loads.clear()
    apps = listOf(app("self", 101, "One"))
    check(checkNotNull(read(changes = LocalAppCatalog.Changes(5, emptySet()))).entries.none { it.packageName == "two" } && loads.isEmpty())
    apps = listOf(app("self", 101, "本机"))
    check(checkNotNull(read("zh")).entries.single().label == "本机" && loads == listOf("self"))
    loads.clear()
    apps = listOf(app("self", 101, "新名字", "new-apk"))
    check(checkNotNull(read("zh", changes = LocalAppCatalog.Changes(6, setOf("elsewhere")))).entries.single().label == "新名字" && loads == listOf("self"))
    loads.clear()
    apps = listOf(app("self", 109, "Reinstalled", "new-apk"))
    check(checkNotNull(read("zh", force = true, changed = setOf("elsewhere"))).entries.single().uid == 109 && loads == listOf("self"))
    loads.clear()
    check(read("zh", changes = LocalAppCatalog.Changes(7, setOf("self")), cancel = { true }) == null && cache.sequence == 6 && loads.isEmpty())
    check(checkNotNull(read("zh", failed = true)).entries.single().uid == 109 && loads == listOf("self"))
    println("Launcher catalog fixture: 12 unchanged views, 0 additional launcher queries, 0 additional label loads.")
}

private fun checkPermissionState() {
    val state = PermissionState(appUid = 10123)
    val initial = state.snapshot()
    check(initial.active == Backend.STANDARD)
    check(initial.backends.getValue(Backend.STANDARD).uid == 10123)
    check(initial.capabilities.keys == Capability.entries.toSet())
    check(initial.capabilities.values.all { it == CapabilityStatus.NOT_IMPLEMENTED })
    check(!state.activate(Backend.DIRECT_ROOT))

    val shell = Backend.SHIZUKU_SHELL
    state.observe(shell, BackendReading(BackendStatus.NEEDS_AUTHORIZATION, 2000))
    check(!state.activate(shell))
    state.observe(shell, BackendReading(BackendStatus.DENIED, 2000))
    check(state.snapshot().active == Backend.STANDARD)
    state.observe(shell, BackendReading(BackendStatus.READY, 2000))
    check(state.snapshot().active == Backend.STANDARD)
    check(state.activate(shell))
    val activated = state.snapshot()
    check(activated.generation == initial.generation + 1)
    check(state.activate(shell) && state.snapshot() == activated)

    state.observe(shell, BackendReading(BackendStatus.LOST))
    val lost = state.snapshot()
    check(lost.active == Backend.STANDARD && lost.generation == activated.generation + 1)
    check(activated.backends.getValue(shell).status == BackendStatus.READY)
    state.observe(shell, BackendReading(BackendStatus.READY, 2000))
    check(state.snapshot().active == Backend.STANDARD)

    val shizukuRoot = Backend.SHIZUKU_ROOT
    check(runCatching { state.observe(shizukuRoot, BackendReading(BackendStatus.READY, 2000)) }.isFailure)
    state.observe(shizukuRoot, BackendReading(BackendStatus.READY, 0))
    check(state.activate(shizukuRoot))
    state.observe(Backend.DIRECT_ROOT, BackendReading(BackendStatus.TIMED_OUT))
    check(!state.activate(Backend.DIRECT_ROOT) && state.snapshot().active == shizukuRoot)
    state.observe(Backend.DIRECT_ROOT, BackendReading(BackendStatus.READY, 0))
    check(state.activate(Backend.DIRECT_ROOT))
    state.observe(Backend.DIRECT_ROOT, BackendReading(BackendStatus.UNCHECKED))
    check(state.snapshot().active == Backend.STANDARD)
    check(state.snapshot().capabilities == initial.capabilities)
}

private fun checkCapabilityLeasesAndRevisions() {
    val baseline: (Backend) -> Map<Capability, CapabilityEntry> = { backend ->
        mapOf(
            Capability.AUDIO_DEVICES to CapabilityEntry(
                CapabilityStatus.AVAILABLE,
                scope = "local_outputs",
                limits = mapOf("count" to "2")
            ),
            Capability.PHONE_VOLUME to CapabilityEntry(
                CapabilityStatus.AVAILABLE,
                scope = "system_media",
                limits = mapOf("range" to "0..15")
            ),
            Capability.ROUTE_OTHER_APPS to if (backend == Backend.STANDARD) {
                CapabilityEntry(CapabilityStatus.UNSUPPORTED, reason = "no_public_api")
            } else {
                CapabilityEntry(CapabilityStatus.NOT_IMPLEMENTED, reason = "stage4_pending")
            }
        )
    }
    val state = PermissionState(appUid = 10440, baselineCapabilities = baseline)
    val snap0 = state.snapshot()
    check(snap0.entry(Capability.AUDIO_DEVICES).status == CapabilityStatus.AVAILABLE)
    check(snap0.entry(Capability.ROUTE_OTHER_APPS).status == CapabilityStatus.UNSUPPORTED)
    check(snap0.entry(Capability.FILE_TRANSFER).status == CapabilityStatus.NOT_IMPLEMENTED)

    // Scope enforcement: system_media cannot authorize owned_track or other_app.
    val wrongScope = snap0.acquireLease(Capability.PHONE_VOLUME, "owned_track", snap0.capabilityRevision)
    check(wrongScope is OperationResult.Failure && wrongScope.error == ProtocolError.PERMISSION_REQUIRED)

    // Stale capability revision is rejected with STALE_SESSION.
    val staleRev = snap0.acquireLease(Capability.PHONE_VOLUME, "system_media", snap0.capabilityRevision + 1)
    check(staleRev is OperationResult.Failure && staleRev.error == ProtocolError.STALE_SESSION)

    val volLease = (snap0.acquireLease(Capability.PHONE_VOLUME, "system_media", snap0.capabilityRevision)
        as OperationResult.Done).value
    check(volLease.isValidFor(snap0))

    // Codex review regression test: capability stays AVAILABLE, but limits or scope change -> old lease MUST invalidate!
    check(
        state.setCapability(
            snap0.generation,
            Capability.PHONE_VOLUME,
            CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "system_media", limits = mapOf("range" to "0..150"))
        )
    )
    val snap1 = state.snapshot()
    check(snap1.capabilityRevision == snap0.capabilityRevision + 1)
    check(!volLease.isValidFor(snap1, requireExactCapabilityRevision = true))
    check(!volLease.isValidFor(snap1, requireExactCapabilityRevision = false)) // limits changed!

    // Updating another capability bumps capabilityRevision: exact-revision write lease invalidates,
    // while unaffected long-lived lease with same scope/limits survives relaxed check.
    val devLease = (snap1.acquireLease(Capability.AUDIO_DEVICES, "local_outputs", snap1.capabilityRevision)
        as OperationResult.Done).value
    check(
        state.setCapability(
            snap1.generation,
            Capability.PLAY_AUDIO,
            CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "owned_track")
        )
    )
    val snap2 = state.snapshot()
    check(!devLease.isValidFor(snap2, requireExactCapabilityRevision = true))
    check(devLease.isValidFor(snap2, requireExactCapabilityRevision = false))

    // Activating Shizuku Root increments generation, resets capabilities, and invalidates ALL prior leases.
    state.observe(Backend.SHIZUKU_ROOT, BackendReading(BackendStatus.READY, 0))
    check(state.activate(Backend.SHIZUKU_ROOT))
    val snap3 = state.snapshot()
    check(snap3.generation == snap2.generation + 1)
    check(!devLease.isValidFor(snap3, requireExactCapabilityRevision = false))
    check(snap3.entry(Capability.ROUTE_OTHER_APPS).status == CapabilityStatus.NOT_IMPLEMENTED)

    // Dropping Shizuku Root falls back to STANDARD cleanly.
    state.observe(Backend.SHIZUKU_ROOT, BackendReading(BackendStatus.LOST))
    val snap4 = state.snapshot()
    check(snap4.active == Backend.STANDARD)
    check(snap4.entry(Capability.ROUTE_OTHER_APPS).status == CapabilityStatus.UNSUPPORTED)
}

private fun checkAudioSinkNormalizer() {
    val raw = listOf(
        RawOutputDevice(3, 2, AudioOutputKind.SPEAKER, "Xiaomi 13", "", isDefaultMedia = true),
        RawOutputDevice(7, 7, AudioOutputKind.BLUETOOTH, "Pixel Buds", "AA:BB:CC:DD:EE:FF", isFallbackBluetooth = true),
        RawOutputDevice(8, 8, AudioOutputKind.BLUETOOTH, "Pixel Buds", "AA:BB:CC:DD:EE:FF", isPrimaryBluetooth = true),
        RawOutputDevice(9, 26, AudioOutputKind.BLUETOOTH, "BLE Buds", "00:00:00:00:00:00", isPrimaryBluetooth = true),
        RawOutputDevice(12, 3, AudioOutputKind.WIRED, "Wired Headset", ""),
        RawOutputDevice(15, 22, AudioOutputKind.USB, "USB-C DAC", "card=1:dev=0")
    )
    val sinks = AudioSinkNormalizer.normalize(raw)
    check(sinks[0].key == AudioOutputSink.KEY_CURRENT && sinks[0].windowsCompatible)
    check(sinks[1].key == AudioOutputSink.KEY_SPEAKER && sinks[1].androidDeviceId == 3 && sinks[1].isDefault)
    val btSinks = sinks.filter { it.kind == AudioOutputKind.BLUETOOTH }
    check(btSinks.size == 2)
    check(btSinks[0].key == "bt:AA:BB:CC:DD:EE:FF" && !btSinks[0].isFallbackProfile && btSinks[0].windowsCompatible)
    check(btSinks[1].key == "bt:BLE_Buds" && btSinks[1].address == null)
    val wired = sinks.single { it.kind == AudioOutputKind.WIRED }
    val usb = sinks.single { it.kind == AudioOutputKind.USB }
    check(wired.key.startsWith("wired:") && !wired.windowsCompatible)
    check(usb.key == "usb:card_1:dev_0" && !usb.windowsCompatible)
    check(AudioSinkNormalizer.findSink(sinks, "bt:Pixel_Buds")?.androidDeviceId == 8)
}

private fun checkSystemVolumeMath() {
    check(SystemVolumeSnapshot.computePercent(0, 0, 15) == 0)
    check(SystemVolumeSnapshot.computePercent(15, 0, 15) == 100)
    check(SystemVolumeSnapshot.computePercent(7, 0, 15) == 46)
    check(SystemVolumeSnapshot.indexFromPercent(0, 0, 15) == 0)
    check(SystemVolumeSnapshot.indexFromPercent(100, 0, 15) == 15)
    check(SystemVolumeSnapshot.indexFromPercent(50, 0, 15) == 8)
    check(SystemVolumeSnapshot.computePercent(1, 1, 10) == 0)
    check(SystemVolumeSnapshot.indexFromPercent(0, 1, 10) == 1)
    check(SystemVolumeSnapshot.indexFromPercent(100, 1, 10) == 10)
}
