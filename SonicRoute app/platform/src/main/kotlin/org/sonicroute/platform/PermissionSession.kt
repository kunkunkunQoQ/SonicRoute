package org.sonicroute.platform

import android.os.Process
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendReading
import org.sonicroute.core.BackendStatus
import org.sonicroute.core.Capability
import org.sonicroute.core.CapabilityEntry
import org.sonicroute.core.CapabilityStatus
import org.sonicroute.core.PermissionSnapshot
import org.sonicroute.core.PermissionState

/** One foreground owner. All calls and callbacks are serialized on the main looper. */
class PermissionSession(private val onChange: (PermissionSnapshot) -> Unit) : AutoCloseable {
    private val state = PermissionState(Process.myUid(), ::baselineCapabilities)
    private val shizuku = ShizukuAccess(::onShizukuChanged)
    private var rootProbe: DirectRootProbe? = null
    private var closed = false

    fun start() {
        shizuku.start()
        publish()
    }

    fun refresh() {
        if (!closed) shizuku.refresh()
    }

    fun currentSnapshot(): PermissionSnapshot = state.snapshot()

    fun updateCapabilities(
        expectedGeneration: Long,
        updates: Map<Capability, CapabilityEntry>
    ): Boolean {
        if (closed) return false
        val changed = state.updateCapabilities(expectedGeneration, updates)
        if (changed) publish()
        return changed
    }

    fun select(backend: Backend) {
        if (closed) return
        if (backend == Backend.DIRECT_ROOT) {
            if (rootProbe != null) return
            shizuku.cancelSelection()
            if (state.snapshot().active == Backend.DIRECT_ROOT) state.activate(Backend.STANDARD)
            state.observe(Backend.DIRECT_ROOT, BackendReading(BackendStatus.VERIFYING))
            rootProbe = DirectRootProbe { reading ->
                rootProbe = null
                state.observe(Backend.DIRECT_ROOT, reading)
                state.activate(Backend.DIRECT_ROOT)
                publish()
            }.also { it.start() }
        } else {
            cancelRoot()
            when (backend) {
                Backend.STANDARD -> {
                    shizuku.cancelSelection()
                    state.activate(Backend.STANDARD)
                }
                Backend.SHIZUKU_SHELL, Backend.SHIZUKU_ROOT -> shizuku.select(backend)
                Backend.DIRECT_ROOT -> error("Direct root is handled by the explicit probe")
            }
        }
        publish()
    }

    private fun cancelRoot() {
        val hadProbe = rootProbe != null
        rootProbe?.close()
        rootProbe = null
        if (hadProbe || state.snapshot().active == Backend.DIRECT_ROOT) {
            state.observe(Backend.DIRECT_ROOT, BackendReading(BackendStatus.UNCHECKED))
        }
    }

    private fun onShizukuChanged(shell: BackendReading, root: BackendReading, selected: Backend?) {
        if (closed) return
        state.observe(Backend.SHIZUKU_SHELL, shell)
        state.observe(Backend.SHIZUKU_ROOT, root)
        if (selected != null) state.activate(selected)
        publish()
    }

    private fun publish() = onChange(state.snapshot())

    override fun close() {
        if (closed) return
        closed = true
        shizuku.close()
        cancelRoot()
        state.activate(Backend.STANDARD)
    }

    companion object {
        fun initialStandardSnapshot(): PermissionSnapshot =
            PermissionState(Process.myUid(), ::baselineCapabilities).snapshot()

        internal fun baselineCapabilities(backend: Backend): Map<Capability, CapabilityEntry> {
            val isStandard = backend == Backend.STANDARD
            return mapOf(
                Capability.ADB_ALIAS to CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "paired_profile"),
                Capability.AUDIO_DEVICES to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "public_audio_manager",
                    scope = "local_outputs"
                ),
                Capability.PLAY_AUDIO to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "public_audio_track",
                    scope = "owned_track",
                    limits = mapOf("pcm" to org.sonicroute.core.SrapPcmInput.FORMAT)
                ),
                Capability.ROUTE_PLAYBACK to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "audio_track_preferred_device",
                    scope = "owned_track"
                ),
                Capability.PHONE_VOLUME to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "stream_music_api",
                    scope = "system_media"
                ),
                Capability.APP_CATALOG to CapabilityEntry(
                    CapabilityStatus.UNAVAILABLE,
                    reason = "catalog_loading",
                    scope = "launcher_intents"
                ),
                Capability.BLUETOOTH_CONTROL to CapabilityEntry(
                    CapabilityStatus.UNAVAILABLE,
                    reason = "awaiting_runtime_check",
                    scope = "bluetooth_audio"
                ),
                Capability.ROUTE_OTHER_APPS to if (isStandard) {
                    CapabilityEntry(
                        CapabilityStatus.UNSUPPORTED,
                        reason = "no_public_per_app_route_api",
                        scope = "other_app"
                    )
                } else {
                    CapabilityEntry(
                        CapabilityStatus.NOT_IMPLEMENTED,
                        reason = "other_app_route_phone_local_only",
                        scope = "other_app"
                    )
                },
                Capability.VOLUME_OTHER_APPS to if (isStandard) {
                    CapabilityEntry(
                        CapabilityStatus.UNSUPPORTED,
                        reason = "no_public_per_app_volume_api",
                        scope = "other_app"
                    )
                } else {
                    CapabilityEntry(
                        CapabilityStatus.UNAVAILABLE,
                        reason = "no_restorable_application_volume_api",
                        scope = "other_app"
                    )
                },
                Capability.CAPTURE_APP to if (isStandard) {
                    CapabilityEntry(
                        CapabilityStatus.NOT_IMPLEMENTED,
                        reason = "stage4_mediaprojection_pending",
                        scope = "same_user_allowed_apps"
                    )
                } else {
                    CapabilityEntry(
                        CapabilityStatus.NOT_IMPLEMENTED,
                        reason = "stage4_uid_capture_pending",
                        scope = "uid_loopback"
                    )
                },
                Capability.VIRTUAL_DISPLAY to CapabilityEntry(
                    CapabilityStatus.UNSUPPORTED, reason = "dedicated_app_display_requires_system_role", scope = "isolated_app"
                ),
                Capability.REMOTE_INPUT to CapabilityEntry(
                    CapabilityStatus.UNSUPPORTED, reason = "no_uid_scoped_input_display", scope = "isolated_app"
                ),
                Capability.EXCLUSIVE_CAPTURE to if (isStandard) {
                    CapabilityEntry(
                        CapabilityStatus.UNSUPPORTED,
                        reason = "no_public_exclusive_capture",
                        scope = "uid_loopback"
                    )
                } else {
                    CapabilityEntry(
                        CapabilityStatus.NOT_IMPLEMENTED,
                        reason = "stage4_exclusive_capture_pending",
                        scope = "uid_loopback"
                    )
                }
            )
        }
    }
}
