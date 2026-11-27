package org.sonicroute.core

enum class Backend(val wireId: String) {
    STANDARD("standard"),
    SHIZUKU_SHELL("shizuku.shell"),
    SHIZUKU_ROOT("shizuku.root"),
    DIRECT_ROOT("root.direct")
}

enum class BackendStatus {
    UNCHECKED, UNAVAILABLE, INCOMPATIBLE, NEEDS_AUTHORIZATION, VERIFYING, READY,
    DENIED, LOST, FAILED, TIMED_OUT
}

data class BackendReading(val status: BackendStatus, val uid: Int? = null)

enum class Capability(val wireId: String) {
    ADB_ALIAS("identity.alias.adb"),
    APP_CATALOG("catalog.apps"),
    MEDIA_CONTROL("media.control"),
    PHONE_VOLUME("volume.device"),
    CAPTURE_DEVICE("audio.capture.device"),
    CAPTURE_APP("audio.capture.app"),
    EXCLUSIVE_CAPTURE("audio.capture.exclusive"),
    PLAY_AUDIO("audio.play"),
    AUDIO_DEVICES("audio.devices"),
    ROUTE_PLAYBACK("audio.route.owned"),
    ROUTE_OTHER_APPS("audio.route.other_app"),
    VOLUME_OTHER_APPS("audio.volume.other_app"),
    BLUETOOTH_CONTROL("bluetooth.control"),
    MIRROR_DISPLAY("display.mirror"),
    VIRTUAL_DISPLAY("display.virtual"),
    REMOTE_INPUT("display.input"),
    FILE_TRANSFER("files.transfer"),
    CLIPBOARD_SYNC("clipboard.sync"),
    AUTOMATION("automation.rules");

    companion object {
        private val byWireId = entries.associateBy { it.wireId }
        fun fromWireId(id: String): Capability? = byWireId[id]
    }
}

enum class CapabilityStatus(val wireId: String) {
    NOT_IMPLEMENTED("not_implemented"),
    NEEDS_AUTHORIZATION("needs_authorization"),
    UNSUPPORTED("unsupported"),
    UNAVAILABLE("unavailable"),
    AVAILABLE("available");

    companion object {
        private val byWireId = entries.associateBy { it.wireId }
        fun fromWireId(id: String): CapabilityStatus? = byWireId[id]
    }
}

data class CapabilityEntry(
    val status: CapabilityStatus,
    val reason: String? = null,
    val scope: String? = null,
    val limits: Map<String, String> = emptyMap()
)

/** Shared cross-platform error taxonomy aligned with docs/protocol.md. */
enum class ProtocolError(val wireId: String) {
    NOT_IMPLEMENTED("not_implemented"),
    PERMISSION_REQUIRED("permission_required"),
    PERMISSION_LOST("permission_lost"),
    UNSUPPORTED("unsupported"),
    TARGET_UNAVAILABLE("target_unavailable"),
    IDENTITY_CONFLICT("identity_conflict"),
    NOT_PAIRED("not_paired"),
    VERSION_MISMATCH("version_mismatch"),
    STALE_SESSION("stale_session"),
    INVALID_ARGUMENT("invalid_argument"),
    BUSY("busy"),
    TIMEOUT("timeout"),
    CANCELED("canceled"),
    IO_FAILURE("io_failure");

    companion object {
        private val byWireId = entries.associateBy { it.wireId }
        fun fromWireId(id: String): ProtocolError? = byWireId[id]
    }
}

sealed interface OperationResult<out T> {
    data class Done<T>(val value: T, val detail: String? = null) : OperationResult<T>
    data class Pending(val detail: String? = null) : OperationResult<Nothing>
    data class Failure(
        val error: ProtocolError,
        val reasonCode: String? = null,
        val retryable: Boolean = false
    ) : OperationResult<Nothing>
}

/**
 * Short-lived execution lease bound to backend identity, backend generation, capability revision,
 * and the authorized entry scope/limits snapshot at acquisition time.
 */
data class OperationLease(
    val backend: Backend,
    val generation: Long,
    val capabilityRevision: Long,
    val capability: Capability,
    val scope: String,
    val authorizedEntryScope: String? = null,
    val authorizedLimits: Map<String, String> = emptyMap()
) {
    fun isValidFor(
        snapshot: PermissionSnapshot,
        requireExactCapabilityRevision: Boolean = true
    ): Boolean {
        if (snapshot.active != backend || snapshot.generation != generation) return false
        if (requireExactCapabilityRevision && snapshot.capabilityRevision != capabilityRevision) return false
        val current = snapshot.entry(capability)
        if (current.status != CapabilityStatus.AVAILABLE) return false
        if (current.scope != authorizedEntryScope) return false
        if (current.limits != authorizedLimits) return false
        if (current.scope != null && scope.isNotEmpty() && !isScopeAuthorized(current.scope, scope)) return false
        return true
    }

    companion object {
        fun isScopeAuthorized(authorizedScope: String, requestedScope: String): Boolean {
            if (requestedScope == authorizedScope) return true
            if (requestedScope.startsWith("$authorizedScope:")) return true
            return false
        }
    }
}

data class PermissionSnapshot(
    val active: Backend,
    val generation: Long,
    val backends: Map<Backend, BackendReading>,
    val capabilityRevision: Long = 0L,
    val capabilityEntries: Map<Capability, CapabilityEntry> = defaultCapabilityEntries
) {
    val capabilities: Map<Capability, CapabilityStatus> =
        capabilityEntries.mapValues { it.value.status }

    fun entry(capability: Capability): CapabilityEntry =
        capabilityEntries[capability] ?: defaultNotImplementedEntry

    fun acquireLease(
        capability: Capability,
        scope: String,
        expectedCapabilityRevision: Long? = null
    ): OperationResult<OperationLease> {
        if (expectedCapabilityRevision != null && expectedCapabilityRevision != capabilityRevision) {
            return OperationResult.Failure(
                ProtocolError.STALE_SESSION,
                "capability_revision_mismatch",
                retryable = true
            )
        }
        val current = entry(capability)
        return when (current.status) {
            CapabilityStatus.AVAILABLE -> {
                if (current.scope != null && scope.isNotEmpty() &&
                    !OperationLease.isScopeAuthorized(current.scope, scope)
                ) {
                    OperationResult.Failure(
                        ProtocolError.PERMISSION_REQUIRED,
                        "scope_not_authorized"
                    )
                } else {
                    OperationResult.Done(
                        OperationLease(
                            backend = active,
                            generation = generation,
                            capabilityRevision = capabilityRevision,
                            capability = capability,
                            scope = scope,
                            authorizedEntryScope = current.scope,
                            authorizedLimits = current.limits
                        )
                    )
                }
            }
            CapabilityStatus.NOT_IMPLEMENTED -> OperationResult.Failure(
                ProtocolError.NOT_IMPLEMENTED, current.reason
            )
            CapabilityStatus.NEEDS_AUTHORIZATION -> OperationResult.Failure(
                ProtocolError.PERMISSION_REQUIRED, current.reason
            )
            CapabilityStatus.UNSUPPORTED -> OperationResult.Failure(
                ProtocolError.UNSUPPORTED, current.reason
            )
            CapabilityStatus.UNAVAILABLE -> OperationResult.Failure(
                ProtocolError.TARGET_UNAVAILABLE, current.reason, retryable = true
            )
        }
    }
}

private val defaultNotImplementedEntry = CapabilityEntry(CapabilityStatus.NOT_IMPLEMENTED)
private val defaultCapabilityEntries: Map<Capability, CapabilityEntry> =
    Capability.entries.associateWith { defaultNotImplementedEntry }

/** Single owner on the caller's event thread. Generation is local to this owner. */
class PermissionState(
    appUid: Int,
    private val baselineCapabilities: (Backend) -> Map<Capability, CapabilityEntry> = { defaultCapabilityEntries }
) {
    private val readings = Backend.entries.associateWith {
        BackendReading(BackendStatus.UNCHECKED)
    }.toMutableMap()
    private var active = Backend.STANDARD
    private var generation = 0L
    private var capabilityRevision = 0L
    private val capabilityEntries = defaultCapabilityEntries.toMutableMap()

    init {
        require(appUid >= 0)
        readings[Backend.STANDARD] = BackendReading(BackendStatus.READY, appUid)
        resetCapabilitiesFor(active, bumpRevision = false)
    }

    fun snapshot() = PermissionSnapshot(
        active = active,
        generation = generation,
        backends = readings.toMap(),
        capabilityRevision = capabilityRevision,
        capabilityEntries = capabilityEntries.toMap()
    )

    fun observe(backend: Backend, reading: BackendReading) {
        require(backend != Backend.STANDARD)
        if (reading.status == BackendStatus.READY) {
            val expectedUid = if (backend == Backend.SHIZUKU_SHELL) 2000 else 0
            require(reading.uid == expectedUid)
        }
        readings[backend] = reading
        if (active == backend && reading.status != BackendStatus.READY) activate(Backend.STANDARD)
    }

    fun activate(backend: Backend): Boolean {
        if (readings.getValue(backend).status != BackendStatus.READY) return false
        if (active != backend) {
            active = backend
            generation++
            resetCapabilitiesFor(backend, bumpRevision = true)
        }
        return true
    }

    fun updateCapabilities(
        expectedGeneration: Long,
        updates: Map<Capability, CapabilityEntry>
    ): Boolean {
        if (expectedGeneration != generation) return false
        var changed = false
        for ((capability, entry) in updates) {
            if (capabilityEntries[capability] != entry) {
                capabilityEntries[capability] = entry
                changed = true
            }
        }
        if (changed) capabilityRevision++
        return changed
    }

    fun setCapability(
        expectedGeneration: Long,
        capability: Capability,
        entry: CapabilityEntry
    ): Boolean = updateCapabilities(expectedGeneration, mapOf(capability to entry))

    private fun resetCapabilitiesFor(backend: Backend, bumpRevision: Boolean) {
        val next = defaultCapabilityEntries.toMutableMap()
        next.putAll(baselineCapabilities(backend))
        var changed = false
        for (capability in Capability.entries) {
            val target = next.getValue(capability)
            if (capabilityEntries[capability] != target) {
                capabilityEntries[capability] = target
                changed = true
            }
        }
        if (bumpRevision && changed) capabilityRevision++
    }
}
