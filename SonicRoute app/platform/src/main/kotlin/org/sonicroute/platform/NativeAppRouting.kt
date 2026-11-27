package org.sonicroute.platform

import android.content.ComponentName
import android.content.Context
import android.content.ServiceConnection
import android.media.AudioManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import moe.shizuku.server.IShizukuService
import org.sonicroute.core.AppCatalogEntry
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendStatus
import org.sonicroute.core.PermissionSnapshot
import rikka.shizuku.Shizuku

data class NativeAppOutput(val id: Int, val type: Int, val address: String, val name: String)

data class NativeAppRoutingState(
    val phase: String = "idle",
    val reason: String = "",
    val packageName: String = "",
    val targetId: Int = 0,
    val originalDeviceIds: List<Int> = emptyList(),
    val actualDeviceIds: List<Int> = emptyList()
) {
    val active: Boolean get() = phase in setOf("applying", "verified", "restoring")
}

/** One local user's selection owns one non-daemon Shizuku service and its render policy. */
internal class NativeAppRouting(private val context: Context, private val changed: () -> Unit) {
    private val main = Handler(Looper.getMainLooper())
    private var binding: Binding? = null
    var state = NativeAppRoutingState()
        private set

    fun physicalOutputs(): List<NativeAppOutput> = runCatching {
        val audio = context.getSystemService(AudioManager::class.java)
        audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS).filter(NativeAppRoutingService::physical).map {
            NativeAppOutput(it.id, it.type, it.address.orEmpty(), it.productName?.toString().orEmpty())
        }
    }.getOrDefault(emptyList())

    fun apply(app: AppCatalogEntry, output: NativeAppOutput, snapshot: PermissionSnapshot, savedSigner: String? = null) {
        if (binding != null) return
        val candidate = NativeAppRoutingState("applying", "", app.packageName, output.id)
        if (!ready(snapshot)) { update(candidate.copy(phase = "error", reason = "routing_permission_required")); return }
        if (output !in physicalOutputs()) { update(candidate.copy(phase = "error", reason = "target_disconnected")); return }
        val signer = runCatching { NativeAppRoutingIdentity.packageSigner(context, app.packageName) }.getOrNull()
        if (signer == null || savedSigner != null && signer != savedSigner) {
            update(candidate.copy(phase = "error", reason = "ambiguous_app_identity")); return
        }
        val next = Binding(app, signer, output, snapshot.active, snapshot.generation)
        binding = next
        update(candidate)
        main.postDelayed(next.timeout, 10000L)
        try {
            if (snapshot.active == Backend.DIRECT_ROOT) {
                next.direct = NativeDirectAppRouting(context, app, signer, output, next.observer, {
                    main.post { if (binding === next) next.connected = true }
                }) {
                    main.post { finish(next, "released", "backend_closed") }
                }.also { it.start() }
            } else Shizuku.bindUserService(next.args, next)
        } catch (_: Exception) { finish(next, "error", "backend_unavailable") }
    }

    fun backendChanged(s: PermissionSnapshot) {
        val b = binding ?: return
        if (s.active != b.backend || s.generation != b.generation || s.backends[s.active]?.status != BackendStatus.READY) {
            finish(b, "released", "backend_closed")
        }
    }

    fun restore() {
        val b = binding ?: return
        if (state.phase == "restoring") return
        val api = b.api
        if (api == null && b.direct == null) { finish(b, "released", "operation_canceled"); return }
        main.removeCallbacks(b.timeout)
        main.postDelayed(b.timeout, 5000L)
        update(state.copy(phase = "restoring", reason = ""))
        try {
            val direct = b.direct
            if (direct != null) direct.restore() else api?.restoreRoute(b.observer)
        } catch (_: Exception) { finish(b, "released", "backend_closed") }
    }

    private fun ready(s: PermissionSnapshot): Boolean {
        if (s.backends[s.active]?.status != BackendStatus.READY) return false
        return if (s.active == Backend.DIRECT_ROOT) s.backends[Backend.DIRECT_ROOT]?.uid == 0 else shizukuGranted(s.active)
    }

    private fun shizukuGranted(backend: Backend): Boolean {
        val expected = when (backend) { Backend.SHIZUKU_SHELL -> 2000; Backend.SHIZUKU_ROOT -> 0; else -> return false }
        return runCatching {
            val service = Shizuku.getBinder() ?: return@runCatching false
            service.isBinderAlive && Shizuku.getUid() == expected && IShizukuService.Stub.asInterface(service).checkSelfPermission() &&
                Shizuku.getBinder() === service
        }.getOrDefault(false)
    }

    private inner class Binding(val app: AppCatalogEntry, val signer: String, val output: NativeAppOutput, val backend: Backend, val generation: Long)
        : ServiceConnection, IBinder.DeathRecipient {
        val args by lazy(LazyThreadSafetyMode.NONE) {
            Shizuku.UserServiceArgs(ComponentName(context.packageName, NativeAppRoutingService::class.java.name))
                .daemon(false).processNameSuffix("app_route").debuggable(false).version(1)
        }
        var api: INativeAppRouting? = null
        var direct: NativeDirectAppRouting? = null
        var connected = false
        val timeout = Runnable { finish(this, "error", when {
            state.phase == "restoring" -> "release_readback_unavailable"
            !connected -> "backend_connection_timed_out"
            else -> "target_readback_failed"
        }) }
        val observer = object : INativeAppRouteObserver.Stub() {
            override fun routeState(phase: String, reason: String, originalDeviceIds: IntArray, actualDeviceIds: IntArray) { main.post {
                if (binding !== this@Binding) return@post
                val readback = state.copy(phase = phase, reason = reason, originalDeviceIds = originalDeviceIds.toList(),
                    actualDeviceIds = actualDeviceIds.toList())
                when (phase) {
                    "verified" -> if (state.phase == "applying") {
                        if (backend != Backend.DIRECT_ROOT && !shizukuGranted(backend)) {
                            finish(this@Binding, "released", "routing_permission_required")
                            return@post
                        }
                        main.removeCallbacks(timeout)
                        update(readback)
                    }
                    "restored", "released", "error" -> finish(this@Binding, phase, reason, readback)
                }
            } }
        }
        override fun onServiceConnected(name: ComponentName?, service: IBinder?) { main.post {
            if (binding !== this || api != null) return@post
            if (service == null || !service.pingBinder()) { finish(this, "error", "backend_unavailable"); return@post }
            if (!shizukuGranted(backend)) { finish(this, "released", "routing_permission_required"); return@post }
            try {
                val bound = INativeAppRouting.Stub.asInterface(service)
                api = bound
                connected = true
                service.linkToDeath(this, 0)
                bound.applyRoute(app.packageName, app.uid, signer, output.id, output.type, output.address, observer)
            } catch (_: Exception) { finish(this, "error", "backend_unavailable") }
        } }
        override fun binderDied() { main.post { finish(this, "released", "backend_closed") } }
        override fun onServiceDisconnected(name: ComponentName?) = binderDied()
        override fun onBindingDied(name: ComponentName?) = binderDied()
        override fun onNullBinding(name: ComponentName?) { main.post { finish(this, "error", "backend_unavailable") } }
    }

    private fun finish(b: Binding, phase: String, reason: String,
                       result: NativeAppRoutingState = state.copy(phase = phase, reason = reason, actualDeviceIds = emptyList())) {
        if (binding !== b) return
        binding = null
        main.removeCallbacks(b.timeout)
        runCatching { b.api?.asBinder()?.unlinkToDeath(b, 0) }
        if (b.backend == Backend.DIRECT_ROOT) b.direct?.close()
        else runCatching { Shizuku.unbindUserService(b.args, b, true) }
        b.direct = null
        b.api = null
        update(result)
    }

    private fun update(next: NativeAppRoutingState) { state = next; changed() }
}
