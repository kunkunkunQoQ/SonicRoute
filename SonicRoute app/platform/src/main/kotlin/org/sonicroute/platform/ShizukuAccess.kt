package org.sonicroute.platform

import android.content.pm.PackageManager.PERMISSION_GRANTED
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.RemoteException
import moe.shizuku.server.IShizukuService
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendReading
import org.sonicroute.core.BackendStatus
import rikka.shizuku.Shizuku
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

/** One Binder carries either the Shell or the Root backend. Their UID and readiness are independent. */
internal class ShizukuAccess(
    private val onChange: (shell: BackendReading, root: BackendReading, selected: Backend?) -> Unit
) : AutoCloseable {
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor()
    private var binder: IBinder? = null
    private var observed = BackendReading(BackendStatus.UNCHECKED)
    private var revision = 0L
    private var wanted: Backend? = null
    @Volatile private var request: Int? = null
    private var deniedFor: IBinder? = null
    private var closed = false
    private val expiry = Runnable { expireRequest() }
    private val received = Shizuku.OnBinderReceivedListener { refresh() }
    private val dead = Shizuku.OnBinderDeadListener {
        if (!closed) {
            retireBinder()
            refresh()
        }
    }
    private val permission = Shizuku.OnRequestPermissionResultListener { code, result ->
        if (!closed && request == code) {
            val target = wanted
            clearRequest()
            if (result == PERMISSION_GRANTED && binder?.isBinderAlive == true) {
                deniedFor = null
                inspect(target, mayRequest = false)
            } else {
                deniedFor = binder
                wanted = null
                inspect()
            }
        }
    }

    fun start() {
        Shizuku.addBinderDeadListener(dead, main)
        Shizuku.addRequestPermissionResultListener(permission, main)
        Shizuku.addBinderReceivedListener(received, main)
    }

    fun refresh() {
        if (closed) return
        findBinder()
        if (request == null && wanted == null && (binder != null || observed.status == BackendStatus.UNCHECKED)) {
            inspect()
        }
    }

    fun select(backend: Backend) {
        if (closed || request != null) return
        findBinder()
        wanted = backend
        inspect(backend, mayRequest = true)
    }

    private fun findBinder() {
        val current = Shizuku.getBinder()?.takeIf { it.isBinderAlive }
        if (current === binder) return
        if (binder != null) retireBinder()
        binder = current
    }

    fun cancelSelection() {
        if (wanted == null && request == null) return
        wanted = null
        clearRequest()
        inspect()
    }

    private fun retireBinder() {
        revision++
        binder = null
        deniedFor = null
        wanted = null
        clearRequest()
        publish(BackendReading(BackendStatus.LOST))
    }

    private fun inspect(selected: Backend? = null, mayRequest: Boolean = false) {
        val version = ++revision
        val candidate = binder
        if (candidate == null) {
            wanted = null
            val status = if (observed.status == BackendStatus.LOST) BackendStatus.LOST else BackendStatus.UNAVAILABLE
            publish(BackendReading(status))
            return
        }
        worker.execute {
            val result = readBinder(candidate)
            main.post {
                if (!closed && version == revision && candidate === binder) {
                    acceptReading(candidate, result, selected, mayRequest)
                }
            }
        }
    }

    private fun readBinder(candidate: IBinder): BackendReading {
        return try {
            if (Shizuku.getBinder() !== candidate || !candidate.isBinderAlive) {
                return BackendReading(BackendStatus.LOST)
            }
            if (Shizuku.isPreV11() || Shizuku.getVersion() < 11) {
                return BackendReading(BackendStatus.INCOMPATIBLE)
            }
            val uid = Shizuku.getUid()
            if (uid != 0 && uid != 2000) return BackendReading(BackendStatus.INCOMPATIBLE)
            // The SDK convenience method caches a granted result. Query its official AIDL live.
            val granted = IShizukuService.Stub.asInterface(candidate).checkSelfPermission()
            BackendReading(if (granted) BackendStatus.READY else BackendStatus.NEEDS_AUTHORIZATION, uid)
        } catch (_: RemoteException) {
            BackendReading(BackendStatus.LOST)
        } catch (_: IllegalStateException) {
            BackendReading(BackendStatus.LOST)
        } catch (_: SecurityException) {
            BackendReading(BackendStatus.DENIED)
        } catch (_: RuntimeException) {
            BackendReading(BackendStatus.FAILED)
        }
    }

    private fun acceptReading(candidate: IBinder, result: BackendReading, selected: Backend?, mayRequest: Boolean) {
        if (Shizuku.getBinder() !== candidate || !candidate.isBinderAlive) {
            retireBinder()
            refresh()
            return
        }
        val intended = selected != null && selected == wanted && result.uid == expectedUid(selected)
        if (intended && mayRequest && result.status == BackendStatus.NEEDS_AUTHORIZATION) {
            requestPermission(candidate, result)
            return
        }
        wanted = null
        val reading = if (result.status == BackendStatus.NEEDS_AUTHORIZATION && deniedFor === candidate) {
            result.copy(status = BackendStatus.DENIED)
        } else result
        if (reading.status == BackendStatus.READY) deniedFor = null
        publish(reading, selected.takeIf { intended && reading.status == BackendStatus.READY })
    }

    private fun requestPermission(candidate: IBinder, reading: BackendReading) {
        val code = requestCodes.incrementAndGet()
        request = code
        publish(reading.copy(status = BackendStatus.VERIFYING))
        main.postDelayed(expiry, 60_000)
        worker.execute {
            if (request != code || Shizuku.getBinder() !== candidate) return@execute
            try {
                IShizukuService.Stub.asInterface(candidate).requestPermission(code)
            } catch (_: RemoteException) {
                main.post { failRequest(code, reading) }
            } catch (_: RuntimeException) {
                main.post { failRequest(code, reading) }
            }
        }
    }

    private fun failRequest(code: Int, reading: BackendReading) {
        if (closed || request != code) return
        wanted = null
        clearRequest()
        publish(reading.copy(status = BackendStatus.FAILED))
    }

    private fun expireRequest() {
        if (closed || request == null) return
        wanted = null
        clearRequest()
        publish(observed.copy(status = BackendStatus.TIMED_OUT))
    }

    private fun clearRequest() {
        request = null
        main.removeCallbacks(expiry)
    }

    private fun publish(reading: BackendReading, selected: Backend? = null) {
        observed = reading
        fun forBackend(backend: Backend): BackendReading =
            if (reading.uid == null || reading.uid == expectedUid(backend)) reading
            else BackendReading(BackendStatus.UNAVAILABLE)

        onChange(forBackend(Backend.SHIZUKU_SHELL), forBackend(Backend.SHIZUKU_ROOT), selected)
    }

    override fun close() {
        if (closed) return
        closed = true
        clearRequest()
        Shizuku.removeBinderReceivedListener(received)
        Shizuku.removeBinderDeadListener(dead)
        Shizuku.removeRequestPermissionResultListener(permission)
        worker.shutdownNow()
    }

    private companion object {
        val requestCodes = AtomicInteger()

        fun expectedUid(backend: Backend): Int = if (backend == Backend.SHIZUKU_SHELL) 2000 else 0
    }
}
