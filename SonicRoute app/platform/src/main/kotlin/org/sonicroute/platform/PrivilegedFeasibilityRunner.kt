package org.sonicroute.platform

import android.content.ComponentName
import android.content.Context
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.media.AudioManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.Process
import org.sonicroute.core.AudioOutputKind
import org.sonicroute.core.Backend
import org.sonicroute.core.Capability
import org.sonicroute.core.CapabilityEntry
import org.sonicroute.core.CapabilityStatus
import org.sonicroute.core.FeasibilityCheckItem
import org.sonicroute.core.FeasibilityReport
import org.sonicroute.core.FeasibilityStatus
import org.sonicroute.core.PermissionSnapshot
import rikka.shizuku.Shizuku
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Runs explicit, non-destructive feasibility verification for per-app audio routing,
 * Bluetooth output, and independent per-app volume under the currently active permission backend.
 * Always unbinds Shizuku UserService (remove=true) and destroys any temporary process upon finish
 * or cancellation so zero background state leaks.
 */
class PrivilegedFeasibilityRunner(
    context: Context,
    private val onReportReady: (FeasibilityReport, Map<Capability, CapabilityEntry>) -> Unit
) : AutoCloseable {

    private val appContext = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor { r ->
        Thread(r, "SonicRoute-feasibility-probe").apply { isDaemon = true }
    }

    @Volatile private var closed = false
    @Volatile private var activeRunId = 0L
    private val activeBinding = AtomicReference<Pair<Shizuku.UserServiceArgs, ServiceConnection>?>(null)

    fun runProbe(
        snapshot: PermissionSnapshot,
        localState: LocalAudioState,
        selfAudioSessionId: Int
    ) {
        if (closed) return
        val runId = ++activeRunId
        unbindActiveShizukuService()

        val activeBackend = snapshot.active
        val generation = snapshot.generation
        val backendUid = snapshot.backends[activeBackend]?.uid
        val romSummary = "${Build.MANUFACTURER} ${Build.MODEL} · Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})"

        worker.execute {
            if (closed || activeRunId != runId) return@execute
            val (items, capUpdates) = when (activeBackend) {
                Backend.STANDARD -> probeStandard(localState)
                Backend.SHIZUKU_SHELL, Backend.SHIZUKU_ROOT ->
                    probeShizuku(activeBackend, localState, selfAudioSessionId, runId)
                Backend.DIRECT_ROOT -> probeDirectRoot(localState)
            }
            val report = FeasibilityReport(
                backend = activeBackend,
                backendUid = backendUid,
                generation = generation,
                apiLevel = Build.VERSION.SDK_INT,
                deviceModel = "${Build.MANUFACTURER} ${Build.MODEL}",
                romSummary = romSummary,
                items = items
            )
            main.post {
                if (!closed && activeRunId == runId) {
                    onReportReady(report, capUpdates)
                }
            }
        }
    }

    fun cancelActive() {
        activeRunId++
        unbindActiveShizukuService()
    }

    private fun probeStandard(
        localState: LocalAudioState
    ): Pair<List<FeasibilityCheckItem>, Map<Capability, CapabilityEntry>> {
        val audioManager = appContext.getSystemService(Context.AUDIO_SERVICE) as AudioManager
        val hasModifyRouting = appContext.checkSelfPermission("android.permission.MODIFY_AUDIO_ROUTING") ==
            PackageManager.PERMISSION_GRANTED
        val playbackCount = runCatching { audioManager.activePlaybackConfigurations.size }.getOrDefault(-1)
        val btOutputs = localState.outputs.filter { it.kind == AudioOutputKind.BLUETOOTH }
        val btConnected = btOutputs.joinToString(", ") { "${it.name}(${it.key})" }.ifEmpty { "none" }

        val items = listOf(
            FeasibilityCheckItem(
                id = "owned_route_bt",
                capability = Capability.ROUTE_PLAYBACK,
                status = FeasibilityStatus.VERIFIED,
                backend = Backend.STANDARD,
                evidence = "outputs=${localState.outputs.size};bt=$btConnected",
                detail = "Public AudioTrack.setPreferredDevice + getRoutedDevice readback ready; BT sinks detected: ${btOutputs.size}."
            ),
            FeasibilityCheckItem(
                id = "owned_gain_vs_system",
                capability = Capability.PLAY_AUDIO,
                status = FeasibilityStatus.VERIFIED,
                backend = Backend.STANDARD,
                evidence = "streamMusic=${localState.volume.streamIndex}/${localState.volume.maxIndex};fixed=${localState.volume.isFixedVolume}",
                detail = "SonicRoute AudioTrack.setVolume() operates independently from STREAM_MUSIC."
            ),
            FeasibilityCheckItem(
                id = "third_party_route",
                capability = Capability.ROUTE_OTHER_APPS,
                status = FeasibilityStatus.UNSUPPORTED,
                backend = Backend.STANDARD,
                evidence = "MODIFY_AUDIO_ROUTING=$hasModifyRouting;uid=${Process.myUid()}",
                detail = "No public API to reroute third-party apps; requires privileged MODIFY_AUDIO_ROUTING + AudioPolicy."
            ),
            FeasibilityCheckItem(
                id = "third_party_volume",
                capability = Capability.VOLUME_OTHER_APPS,
                status = FeasibilityStatus.UNSUPPORTED,
                backend = Backend.STANDARD,
                evidence = "activeConfigs=$playbackCount;uid=${Process.myUid()}",
                detail = "Standard apps cannot inspect other apps' UIDs or invoke PlayerProxy.setVolume()."
            ),
            FeasibilityCheckItem(
                id = "app_catalog_scope",
                capability = Capability.APP_CATALOG,
                status = FeasibilityStatus.VERIFIED,
                backend = Backend.STANDARD,
                evidence = "launcherApps=${localState.appCatalog.entries.size};scope=${localState.appCatalog.visibilityScope}",
                detail = "Constrained launcher-intent visibility active without QUERY_ALL_PACKAGES."
            )
        )
        val caps = mapOf(
            Capability.ROUTE_OTHER_APPS to CapabilityEntry(
                CapabilityStatus.UNSUPPORTED,
                reason = "no_public_per_app_route_api",
                scope = "third_party_uid"
            ),
            Capability.VOLUME_OTHER_APPS to CapabilityEntry(
                CapabilityStatus.UNSUPPORTED,
                reason = "no_public_per_app_volume_api",
                scope = "third_party_uid"
            )
        )
        return items to caps
    }

    private fun probeShizuku(
        backend: Backend,
        localState: LocalAudioState,
        selfAudioSessionId: Int,
        runId: Long
    ): Pair<List<FeasibilityCheckItem>, Map<Capability, CapabilityEntry>> {
        val expectedUid = if (backend == Backend.SHIZUKU_SHELL) 2000 else 0
        val binder = Shizuku.getBinder()?.takeIf { it.isBinderAlive }
        if (binder == null || runCatching { Shizuku.getUid() }.getOrDefault(-1) != expectedUid) {
            return errorItems(backend, "shizuku_binder_unavailable_or_uid_mismatch") to mapOf(
                Capability.ROUTE_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = "shizuku_binder_unavailable"),
                Capability.VOLUME_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = "shizuku_binder_unavailable")
            )
        }

        val args = Shizuku.UserServiceArgs(
            ComponentName(appContext.packageName, PrivilegedAudioProbeService::class.java.name)
        )
            .daemon(false)
            .processNameSuffix("audio_probe")
            .debuggable(false)
            .version(1)

        val latch = CountDownLatch(1)
        val serviceRef = AtomicReference<IPrivilegedAudioProbe?>(null)
        val connection = object : ServiceConnection {
            override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
                if (service != null && service.pingBinder()) {
                    serviceRef.set(IPrivilegedAudioProbe.Stub.asInterface(service))
                }
                latch.countDown()
            }

            override fun onServiceDisconnected(name: ComponentName?) {
                serviceRef.set(null)
            }
        }

        activeBinding.set(args to connection)
        val bundle: Bundle? = try {
            Shizuku.bindUserService(args, connection)
            val bound = latch.await(10, TimeUnit.SECONDS)
            val proxy = serviceRef.get()
            if (!bound || proxy == null || closed || activeRunId != runId) {
                null
            } else {
                proxy.probeAudioCapabilities(Process.myUid(), selfAudioSessionId)
            }
        } catch (t: Throwable) {
            Bundle().apply {
                putString(
                    PrivilegedAudioProbeService.KEY_FATAL_ERROR,
                    "${t.javaClass.simpleName}:${t.message.orEmpty()}"
                )
            }
        } finally {
            unbindActiveShizukuService()
        }

        if (bundle == null) {
            return errorItems(backend, "user_service_bind_timeout_or_canceled") to mapOf(
                Capability.ROUTE_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = "probe_timeout"),
                Capability.VOLUME_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = "probe_timeout")
            )
        }

        val fatal = bundle.getString(PrivilegedAudioProbeService.KEY_FATAL_ERROR)
        if (!fatal.isNullOrEmpty()) {
            return errorItems(backend, fatal) to mapOf(
                Capability.ROUTE_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = fatal),
                Capability.VOLUME_OTHER_APPS to CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = fatal)
            )
        }

        val serviceUid = bundle.getInt(PrivilegedAudioProbeService.KEY_SERVICE_UID, -1)
        val seContext = bundle.getString(PrivilegedAudioProbeService.KEY_SELINUX_CONTEXT, "unknown")
        val permRouting = bundle.getBoolean(PrivilegedAudioProbeService.KEY_PERM_MODIFY_AUDIO_ROUTING)
        val permCapture = bundle.getBoolean(PrivilegedAudioProbeService.KEY_PERM_CAPTURE_AUDIO_OUTPUT)
        val permBtPriv = bundle.getBoolean(PrivilegedAudioProbeService.KEY_PERM_BLUETOOTH_PRIVILEGED)
        val installedCount = bundle.getInt(PrivilegedAudioProbeService.KEY_INSTALLED_APPS_COUNT, -1)
        val renderSpeakerOk = bundle.getBoolean(PrivilegedAudioProbeService.KEY_UID_RENDER_SPEAKER_OK)
        val renderSpeakerDetail = bundle.getString(PrivilegedAudioProbeService.KEY_UID_RENDER_SPEAKER_DETAIL, "")
        val renderBtTested = bundle.getBoolean(PrivilegedAudioProbeService.KEY_UID_RENDER_BT_TESTED)
        val renderBtOk = bundle.getBoolean(PrivilegedAudioProbeService.KEY_UID_RENDER_BT_OK)
        val renderBtDetail = bundle.getString(PrivilegedAudioProbeService.KEY_UID_RENDER_BT_DETAIL, "")
        val loopbackOk = bundle.getBoolean(PrivilegedAudioProbeService.KEY_UID_LOOPBACK_OK)
        val loopbackDetail = bundle.getString(PrivilegedAudioProbeService.KEY_UID_LOOPBACK_DETAIL, "")
        val hasAffinityApi = bundle.getBoolean(PrivilegedAudioProbeService.KEY_HAS_SET_UID_DEVICE_AFFINITY)
        val activeConfigs = bundle.getInt(PrivilegedAudioProbeService.KEY_ACTIVE_PLAYBACK_COUNT, -1)
        val visibleUids = bundle.getInt(PrivilegedAudioProbeService.KEY_VISIBLE_PLAYBACK_UID_COUNT, 0)
        val visibleProxies = bundle.getInt(PrivilegedAudioProbeService.KEY_VISIBLE_PLAYER_PROXY_COUNT, 0)
        val hasProxySetVol = bundle.getBoolean(PrivilegedAudioProbeService.KEY_PLAYER_PROXY_SET_VOLUME_API)
        val selfProxyDetail = bundle.getString(PrivilegedAudioProbeService.KEY_PLAYER_PROXY_DETAIL, "")
        val oemVolMethods = bundle.getString(PrivilegedAudioProbeService.KEY_OEM_APP_VOLUME_METHODS, "")

        val btOutputs = localState.outputs.filter { it.kind == AudioOutputKind.BLUETOOTH }

        val routeStatus = when {
            renderSpeakerOk || loopbackOk -> FeasibilityStatus.PARTIAL
            !permRouting && serviceUid == 2000 -> FeasibilityStatus.BLOCKED_PERMISSION
            else -> FeasibilityStatus.BLOCKED_API
        }

        val volumeStatus = when {
            hasProxySetVol || oemVolMethods.isNotEmpty() -> FeasibilityStatus.PARTIAL
            else -> FeasibilityStatus.BLOCKED_API
        }

        val items = listOf(
            FeasibilityCheckItem(
                id = "shizuku_identity",
                capability = Capability.APP_CATALOG,
                status = if (serviceUid == expectedUid) FeasibilityStatus.VERIFIED else FeasibilityStatus.ERROR,
                backend = backend,
                evidence = "uid=$serviceUid;se=$seContext;apps=$installedCount;perms[route=$permRouting,cap=$permCapture,btPriv=$permBtPriv]",
                detail = "UserService executed cleanly and exited on unbind; visible packages=$installedCount (launcher=${localState.appCatalog.entries.size})."
            ),
            FeasibilityCheckItem(
                id = "third_party_route_render",
                capability = Capability.ROUTE_OTHER_APPS,
                status = routeStatus,
                backend = backend,
                evidence = "speaker=$renderSpeakerOk($renderSpeakerDetail);bt=$renderBtOk($renderBtDetail);affinityApi=$hasAffinityApi",
                detail = if (renderSpeakerOk) {
                    "Self-UID AudioPolicy RULE_MATCH_UID + ROUTE_FLAG_RENDER accepted & unregistered cleanly. BT sinks connected=${btOutputs.size}."
                } else {
                    "Direct UID render policy rejected ($renderSpeakerDetail); check loopback re-render path below."
                }
            ),
            FeasibilityCheckItem(
                id = "third_party_exclusive_loopback",
                capability = Capability.EXCLUSIVE_CAPTURE,
                status = if (loopbackOk) FeasibilityStatus.PARTIAL else FeasibilityStatus.BLOCKED_API,
                backend = backend,
                evidence = "loopback=$loopbackOk($loopbackDetail);capturePerm=$permCapture",
                detail = if (loopbackOk) {
                    "The SonicRoute UID loopback sink initialized and was released."
                } else {
                    "UID loopback policy/sink failed ($loopbackDetail)."
                }
            ),
            FeasibilityCheckItem(
                id = "third_party_volume",
                capability = Capability.VOLUME_OTHER_APPS,
                status = volumeStatus,
                backend = backend,
                evidence = "configs=$activeConfigs;uids=$visibleUids;proxies=$visibleProxies;proxySetVol=$hasProxySetVol;selfTest=$selfProxyDetail;oem=[$oemVolMethods]",
                detail = "A restorable per-app volume setter and readback are required. Player gain was left untouched."
            )
        )

        // Strict separation per docs/protocol.md & Codex Stage 1 review:
        // Self-UID probes establish technical feasibility ONLY; unimplemented Stage 4 third-party
        // capabilities MUST NOT be published as AVAILABLE until real target execution & readback exist.
        val caps = LinkedHashMap<Capability, CapabilityEntry>()
        caps[Capability.ROUTE_OTHER_APPS] = if (renderSpeakerOk || loopbackOk) {
            CapabilityEntry(
                CapabilityStatus.NOT_IMPLEMENTED,
                reason = "other_app_route_phone_local_only",
                scope = "other_app",
                limits = mapOf("probe" to "verified", "btDirectRenderTested" to renderBtTested.toString())
            )
        } else {
            CapabilityEntry(
                CapabilityStatus.UNAVAILABLE,
                reason = renderSpeakerDetail.ifEmpty { "audio_policy_rejected" },
                scope = "other_app"
            )
        }
        caps[Capability.EXCLUSIVE_CAPTURE] = if (loopbackOk) {
            CapabilityEntry(
                CapabilityStatus.NOT_IMPLEMENTED,
                reason = "probe_loopback_sink_ok_stage4_pending",
                scope = "uid_loopback",
                limits = mapOf("probe" to "verified")
            )
        } else {
            CapabilityEntry(
                CapabilityStatus.UNAVAILABLE,
                reason = loopbackDetail.ifEmpty { "loopback_sink_unavailable" }.take(128),
                scope = "uid_loopback"
            )
        }
        caps[Capability.CAPTURE_APP] = if (loopbackOk) {
            CapabilityEntry(
                CapabilityStatus.NOT_IMPLEMENTED,
                reason = "probe_loopback_sink_ok_stage4_pending",
                scope = "uid_loopback",
                limits = mapOf("probe" to "verified")
            )
        } else {
            CapabilityEntry(
                CapabilityStatus.UNAVAILABLE,
                reason = loopbackDetail.ifEmpty { "loopback_sink_unavailable" }.take(128),
                scope = "uid_loopback"
            )
        }
        caps[Capability.VOLUME_OTHER_APPS] = CapabilityEntry(CapabilityStatus.UNAVAILABLE,
            reason = "no_restorable_application_volume_api", scope = "other_app")
        // Keep APP_CATALOG scope truthful to what catalog.list currently serves (launcher_intents),
        // recording privileged package visibility count only in limits/feasibility report.
        if (installedCount > 0) {
            caps[Capability.APP_CATALOG] = CapabilityEntry(
                CapabilityStatus.AVAILABLE,
                reason = "launcher_intent_visibility",
                scope = "launcher_intents",
                limits = mapOf(
                    "visibleApps" to localState.appCatalog.entries.size.toString(),
                    "privilegedPackages" to installedCount.toString()
                )
            )
        }

        return items to caps
    }

    private fun probeDirectRoot(
        localState: LocalAudioState
    ): Pair<List<FeasibilityCheckItem>, Map<Capability, CapabilityEntry>> {
        // Fixed, non-interpolated read-only check only; never retains a root shell.
        var process: java.lang.Process? = null
        val (uidOk, seMode, hasAppProcess, rawDetail) = try {
            val child = ProcessBuilder(
                "su", "-c",
                "/system/bin/id -u; /system/bin/getenforce 2>/dev/null || echo unknown; " +
                    "[ -x /system/bin/app_process64 ] && echo app_process64=yes || echo app_process64=no"
            ).start()
            process = child
            child.outputStream.close()
            if (!child.waitFor(10, TimeUnit.SECONDS)) {
                Quad(false, "unknown", false, "timed_out")
            } else if (child.exitValue() != 0) {
                Quad(false, "unknown", false, "exit=${child.exitValue()}")
            } else {
                val out = child.inputStream.bufferedReader(Charsets.US_ASCII).use { reader ->
                    val buf = CharArray(256)
                    val n = reader.read(buf)
                    if (n > 0) String(buf, 0, n) else ""
                }
                val lines = out.lines().map { it.trim() }.filter { it.isNotEmpty() }
                val uid = lines.getOrNull(0)?.toIntOrNull()
                val enforce = lines.getOrNull(1).orEmpty().ifEmpty { "unknown" }
                val appProc = lines.any { it == "app_process64=yes" }
                Quad(uid == 0, enforce, appProc, "uid=$uid;selinux=$enforce;app_process64=$appProc")
            }
        } catch (t: Throwable) {
            Quad(false, "unknown", false, "${t.javaClass.simpleName}:${t.message.orEmpty()}")
        } finally {
            runCatching { process?.destroyForcibly() }
            runCatching { process?.inputStream?.close() }
            runCatching { process?.errorStream?.close() }
            runCatching { process?.outputStream?.close() }
        }

        val items = listOf(
            FeasibilityCheckItem(
                id = "direct_root_env",
                capability = Capability.ROUTE_OTHER_APPS,
                status = if (uidOk && hasAppProcess) FeasibilityStatus.PARTIAL else FeasibilityStatus.BLOCKED_PERMISSION,
                backend = Backend.DIRECT_ROOT,
                evidence = rawDetail,
                detail = if (uidOk && hasAppProcess) {
                    "Direct root UID 0 and app_process64 confirmed (SELinux=$seMode). Select a playing app and physical output on the phone for a live routing check."
                } else {
                    "Direct Root probe unavailable ($rawDetail)."
                }
            ),
            FeasibilityCheckItem(
                id = "direct_root_local_audio",
                capability = Capability.ROUTE_PLAYBACK,
                status = FeasibilityStatus.VERIFIED,
                backend = Backend.DIRECT_ROOT,
                evidence = "outputs=${localState.outputs.size};bt=${localState.outputs.count { it.kind == AudioOutputKind.BLUETOOTH }}",
                detail = "Public owned-track playback, Bluetooth/Wired/USB routing readback, and independent track gain work normally."
            )
        )
        val caps = mapOf(
            Capability.ROUTE_OTHER_APPS to CapabilityEntry(
                CapabilityStatus.NOT_IMPLEMENTED,
                reason = "other_app_route_phone_local_only",
                scope = "third_party_uid"
            ),
            Capability.VOLUME_OTHER_APPS to CapabilityEntry(
                CapabilityStatus.UNAVAILABLE,
                reason = "no_restorable_application_volume_api",
                scope = "third_party_uid"
            )
        )
        return items to caps
    }

    private data class Quad(val ok: Boolean, val se: String, val appProc: Boolean, val detail: String)

    private fun errorItems(backend: Backend, reason: String): List<FeasibilityCheckItem> = listOf(
        FeasibilityCheckItem(
            id = "probe_error",
            capability = Capability.ROUTE_OTHER_APPS,
            status = FeasibilityStatus.ERROR,
            backend = backend,
            evidence = reason,
            detail = "Feasibility probe could not complete under ${backend.wireId}: $reason"
        )
    )

    private fun unbindActiveShizukuService() {
        val current = activeBinding.getAndSet(null) ?: return
        runCatching { Shizuku.unbindUserService(current.first, current.second, true) }
    }

    override fun close() {
        if (closed) return
        closed = true
        activeRunId++
        unbindActiveShizukuService()
        worker.shutdownNow()
    }
}
