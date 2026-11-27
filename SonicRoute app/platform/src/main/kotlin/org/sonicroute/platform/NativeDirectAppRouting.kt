package org.sonicroute.platform

import android.content.Context
import android.net.LocalServerSocket
import android.net.LocalSocket
import android.os.Process
import android.system.Os
import android.system.OsConstants
import org.sonicroute.core.AppCatalogEntry
import org.sonicroute.core.NativeAppRoutingPipe
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

internal class NativeDirectAppRouting(private val context: Context, private val app: AppCatalogEntry, private val signer: String, private val output: NativeAppOutput,
                                      private val observer: INativeAppRouteObserver, private val connected: () -> Unit,
                                      private val disconnected: () -> Unit) : AutoCloseable {
    private val lock = Any()
    private val closed = AtomicBoolean()
    private var server: LocalServerSocket? = null
    private var socket: LocalSocket? = null
    private var process: java.lang.Process? = null
    private var writer: DataOutputStream? = null
    private var restoreRequested = false
    private var failureReason = "unverified_caller"

    fun start() { thread(name = "SonicRoute-owned-root-route") {
        try { connect() } catch (_: Exception) {
            if (!closed.get()) observer.routeState("error", failureReason, intArrayOf(), intArrayOf())
        } finally { close(); disconnected() }
    } }

    private fun connect() {
        val wire = NativeAppRoutingPipe
        require(context.packageName == wire.OWNER && context.packageManager.getPackagesForUid(Process.myUid())?.singleOrNull() == wire.OWNER)
        require(wire.validPackage(app.packageName))
        val apk = NativeAppRoutingIdentity.apk(context)
        val signers = NativeAppRoutingIdentity.signers(context, apk)
        failureReason = "backend_unavailable"
        val address = with(wire) { "sonicroute-route-" + random().hex() }
        val nonce = wire.random()
        try {
            val listener = synchronized(lock) {
                if (closed.get()) return
                LocalServerSocket(address).also { server = it }
            }
            // The only substituted value is the canonical PackageManager install path, restricted
            // to /data/app/.../base.apk and a shell-safe alphabet. All selections stay on the socket.
            val child = ProcessBuilder("su", "-c", "CLASSPATH=$apk exec /system/bin/app_process /system/bin ${wire.ENTRY}")
                .redirectOutput(File("/dev/null")).redirectError(File("/dev/null")).start()
            synchronized(lock) {
                if (closed.get()) { child.destroyForcibly(); return }
                process = child
            }
            DataOutputStream(child.outputStream).use { out ->
                out.writeInt(wire.MAGIC); wire.text(out, address); out.write(nonce); out.write(signers); out.flush()
            }
            val channel = listener.accept()
            synchronized(lock) {
                if (closed.get()) { channel.close(); return }
                socket = channel
                server = null
                listener.close()
            }
            channel.soTimeout = 5000
            failureReason = "direct_peer_identity_unverified"
            require(channel.peerCredentials.uid == 0)
            val input = DataInputStream(channel.inputStream)
            require(input.readInt() == wire.MAGIC)
            require(wire.rootProof(channel.peerCredentials.uid, nonce, ByteArray(32).also(input::readFully), signers,
                ByteArray(32).also(input::readFully)))
            val out = DataOutputStream(channel.outputStream)
            connected()
            failureReason = "backend_closed"
            synchronized(lock) {
                if (closed.get() || restoreRequested) return
                out.writeByte(wire.APPLY); wire.text(out, app.packageName); out.writeInt(app.uid); wire.text(out, signer)
                out.writeInt(output.id); out.writeInt(output.type); wire.text(out, output.address); out.flush()
                writer = out
            }
            channel.soTimeout = 0
            while (!closed.get()) {
                if (input.read() != wire.STATE) return
                val phase = wire.text(input)
                require(phase in wire.phases)
                val reason = wire.text(input)
                val original = wire.ids(input)
                val actual = wire.ids(input)
                observer.routeState(phase, reason, original, actual)
                if (phase != "verified") return
            }
        } finally { nonce.fill(0) }
    }

    fun restore() {
        val pending = synchronized(lock) {
            if (restoreRequested || closed.get()) return
            restoreRequested = true
            writer
        }
        if (pending == null) { close(); return }
        try { pending.writeByte(NativeAppRoutingPipe.RESTORE); pending.flush() }
        catch (_: Exception) { close() }
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        synchronized(lock) {
            writer = null
            server?.let {
                runCatching { Os.shutdown(it.fileDescriptor, OsConstants.SHUT_RDWR) }
                runCatching { it.close() }
            }
            server = null
            socket?.let {
                runCatching { it.shutdownInput() }; runCatching { it.shutdownOutput() }; runCatching { it.close() }
            }
            socket = null
            process?.let {
                runCatching { it.outputStream.close() }; runCatching { it.inputStream.close() }; runCatching { it.errorStream.close() }
                runCatching { it.destroy() }
            }
            process = null
        }
    }
}
