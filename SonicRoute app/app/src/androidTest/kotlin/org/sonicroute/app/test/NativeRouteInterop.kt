package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import android.os.SystemClock
import android.view.View
import org.sonicroute.app.MainActivity
import org.sonicroute.app.R
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendStatus
import org.sonicroute.platform.SonicRouteRuntime
import java.io.File
import java.util.concurrent.FutureTask

/** The host plays a known, non-silent file in a separately installed third-party media player. */
internal class NativeRouteInterop(private val instrument: Instrumentation, private val args: Bundle) {
    private fun <T> ui(action: () -> T): T {
        val work = FutureTask(action)
        instrument.runOnMainSync(work)
        return work.get()
    }
    private fun status(key: String, value: String) =
        instrument.sendStatus(1, Bundle().apply { putString(key, value) })
    private fun await(ms: Long, check: () -> Boolean): Boolean {
        val deadline = SystemClock.elapsedRealtime() + ms
        do { if (check()) return true; SystemClock.sleep(100) } while (SystemClock.elapsedRealtime() < deadline)
        return false
    }

    fun run() {
        if (args.getString("operation") == "volume-api") {
            NativeVolumeApiInterop(instrument).run()
            return
        }
        val result = Bundle()
        var stage = "prepare"
        var cleanup: (() -> Unit)? = null
        var page: Activity? = null
        try {
            val pkg = args.getString("player")
            require(pkg in setOf("bin.mt.plus", "cn.wenyu.bodian"))
            val targetId = requireNotNull(args.getString("target")).toInt()
            val runId = requireNotNull(args.getString("run")).also { require(it.matches(Regex("[a-f0-9]{32}"))) }
            val backend = when (args.getString("backend")) {
                "shizuku_root" -> Backend.SHIZUKU_ROOT
                "shizuku_shell" -> Backend.SHIZUKU_SHELL
                "direct_root" -> Backend.DIRECT_ROOT
                else -> error("unknown_backend")
            }
            val mark = File(instrument.targetContext.cacheDir, "route-$runId.sampled")
            val monitor = instrument.addMonitor(MainActivity::class.java.name, null, false)
            status("launch", "ready")
            val activity = requireNotNull(instrument.waitForMonitorWithTimeout(monitor, 25_000))
            page = activity
            instrument.removeMonitor(monitor)
            instrument.waitForIdleSync()
            val runtime = SonicRouteRuntime.getInstance(instrument.targetContext)
            fun press(id: Int) {
                val control = activity.findViewById<View>(id)
                check(control.isEnabled && control.isShown)
                control.performClick()
            }
            stage = "backend"
            val initial = ui { runtime.currentSnapshot() }
            check(!initial.nativeAppRoute.active && !initial.lifecycle.lanEnabled && initial.permissions.active in setOf(Backend.STANDARD, backend))
            cleanup = {
                ui { if (runtime.currentSnapshot().nativeAppRoute.active) runtime.restoreLocalAppRoute() }
                await(5000) { ui { !runtime.currentSnapshot().nativeAppRoute.active } }
                ui { if (runtime.currentSnapshot().permissions.active != initial.permissions.active) runtime.selectBackend(initial.permissions.active) }
                mark.delete()
            }
            ui {
                press(R.id.nav_settings)
                if (!activity.findViewById<View>(R.id.backend_list).isShown) press(R.id.backend_options)
                press(when (backend) {
                    Backend.SHIZUKU_ROOT -> R.id.backend_shizuku_root
                    Backend.SHIZUKU_SHELL -> R.id.backend_shizuku_shell
                    else -> R.id.backend_direct_root
                })
            }
            check(await(20_000) { ui { runtime.currentSnapshot().permissions.let { it.active == backend && it.backends[backend]?.status == BackendStatus.READY } } })
            ui { press(R.id.nav_audio) }
            stage = "catalog"
            check(ui { runtime.currentSnapshot().audio.catalogState == org.sonicroute.platform.LauncherCatalogState.IDLE })
            ui { press(R.id.native_app_route) }
            check(await(8000) { ui { runtime.currentSnapshot().audio.appCatalog.entries.any { it.packageName == pkg } } })
            stage = "apply"
            ui {
                val app = runtime.currentSnapshot().audio.appCatalog.entries.single { it.packageName == pkg && !it.isSelf }
                val target = runtime.nativeAppOutputs().single { it.id == targetId }
                check(runtime.routeLocalApp(app.packageName, app.uid, target))
            }
            check(await(12_000) { ui { runtime.currentSnapshot().nativeAppRoute.phase in setOf("verified", "error", "released") } })
            val route = ui { runtime.currentSnapshot().nativeAppRoute }
            result.putString("routeReason", route.reason.take(96))
            check(route.phase == "verified" && route.actualDeviceIds.toSet() == setOf(targetId) && targetId !in route.originalDeviceIds)
            status("route", "verified")
            check(await(12_000) { mark.exists() })
            stage = "restore"
            ui { runtime.restoreLocalAppRoute() }
            check(await(10_000) { ui { !runtime.currentSnapshot().nativeAppRoute.active } })
            val restored = ui { runtime.currentSnapshot().nativeAppRoute }
            result.putString("routeReason", restored.reason.take(96))
            check(restored.phase == "restored" && restored.actualDeviceIds.toSet() == route.originalDeviceIds.toSet())
            result.putString("interop", "passed")
        } catch (e: Exception) {
            val problem = e.cause ?: e
            result.putString("interop", "failed-$stage-${problem.javaClass.simpleName}")
        } finally {
            runCatching { cleanup?.invoke() }.onFailure { result.putString("interop", "cleanup-failed") }
            page?.let { a -> ui { a.finish() } }
            instrument.finish(if (result.getString("interop") == "passed") Activity.RESULT_OK else Activity.RESULT_CANCELED, result)
        }
    }
}
