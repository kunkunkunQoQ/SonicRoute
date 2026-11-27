package org.sonicroute.platform

import android.os.Handler
import android.os.Looper
import org.sonicroute.core.BackendReading
import org.sonicroute.core.BackendStatus
import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/** A one-shot identity check. No shell or privilege is retained for a future operation. */
internal class DirectRootProbe(private val onResult: (BackendReading) -> Unit) : AutoCloseable {
    private val main = Handler(Looper.getMainLooper())
    private val lock = ReentrantLock()
    private var closed = false
    private var process: Process? = null
    private val worker = Thread(::run, "SonicRoute-root-probe")

    fun start() = worker.start()

    private fun run() {
        if (lock.withLock { closed }) return
        val reading = try {
            val child = ProcessBuilder("su", "-c", "/system/bin/id -u").start()
            try {
                val attached = lock.withLock {
                    if (closed) false else {
                        process = child
                        true
                    }
                }
                if (!attached) return
                readUid(child)
            } finally {
                child.destroyForcibly()
                runCatching { child.inputStream.close() }
                runCatching { child.errorStream.close() }
                runCatching { child.outputStream.close() }
                lock.withLock { process = null }
            }
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            return
        } catch (_: IOException) {
            BackendReading(BackendStatus.UNAVAILABLE)
        } catch (_: SecurityException) {
            BackendReading(BackendStatus.DENIED)
        }
        main.post {
            if (!lock.withLock { closed }) onResult(reading)
        }
    }

    private fun readUid(child: Process): BackendReading {
        child.outputStream.close()
        if (!child.waitFor(15, TimeUnit.SECONDS)) return BackendReading(BackendStatus.TIMED_OUT)
        if (child.exitValue() != 0) return BackendReading(BackendStatus.FAILED)

        // A descendant can keep a pipe open. Read only the already available bounded output.
        val output = child.inputStream
        val size = output.available()
        if (size !in 1..32) return BackendReading(BackendStatus.FAILED)
        val bytes = ByteArray(size)
        val count = output.read(bytes)
        val uid = if (count == size) String(bytes, Charsets.US_ASCII).trim().toIntOrNull() else null
        return when {
            uid == 0 -> BackendReading(BackendStatus.READY, uid)
            uid != null && uid > 0 -> BackendReading(BackendStatus.DENIED, uid)
            else -> BackendReading(BackendStatus.FAILED)
        }
    }

    override fun close() {
        val child = lock.withLock {
            closed = true
            process
        }
        worker.interrupt()
        child?.destroyForcibly()
    }
}
