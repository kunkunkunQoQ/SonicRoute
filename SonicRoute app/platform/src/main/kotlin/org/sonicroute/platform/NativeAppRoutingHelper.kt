package org.sonicroute.platform

import android.content.Context
import android.net.LocalSocket
import android.net.LocalSocketAddress
import android.os.Handler
import android.os.Looper
import android.os.Process
import android.os.UserHandle
import org.sonicroute.core.NativeAppRoutingPipe
import java.io.DataInputStream
import java.io.DataOutputStream
import java.security.MessageDigest
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.system.exitProcess

/** Fixed app_process entry. It accepts one local MEDIA routing selection and an optional restore. */
object NativeAppRoutingHelper {
    @JvmStatic fun main(args: Array<String>) {
        if (args.isNotEmpty() || Process.myUid() != 0) exitProcess(1)
        val result = runCatching { run() }
        exitProcess(if (result.isSuccess) 0 else 1)
    }

    private fun run() {
        val wire = NativeAppRoutingPipe
        val boot = DataInputStream(System.`in`)
        require(boot.readInt() == wire.MAGIC)
        val address = wire.text(boot)
        require(address.matches(Regex("sonicroute-route-[a-f0-9]{64}")))
        val nonce = ByteArray(32).also(boot::readFully)
        val expectedSigners = ByteArray(32).also(boot::readFully)
        require(boot.read() == -1)
        LocalSocket().use { socket ->
            socket.connect(LocalSocketAddress(address, LocalSocketAddress.Namespace.ABSTRACT))
            socket.soTimeout = 5000
            val owner = socket.peerCredentials.uid
            require(owner % 100000 in Process.FIRST_APPLICATION_UID..Process.LAST_APPLICATION_UID)
            if (Looper.myLooper() == null) Looper.prepareMainLooper()
            val context = ownerContext(owner)
            val actualApk = NativeAppRoutingIdentity.apk(context)
            require(actualApk == System.getenv("CLASSPATH"))
            val signers = NativeAppRoutingIdentity.signers(context, actualApk)
            require(MessageDigest.isEqual(signers, expectedSigners))
            val input = DataInputStream(socket.inputStream)
            val out = DataOutputStream(socket.outputStream)
            out.writeInt(wire.MAGIC); out.write(nonce); out.write(signers); out.flush()
            nonce.fill(0)
            require(input.readUnsignedByte() == wire.APPLY)
            val pkg = wire.text(input)
            val uid = input.readInt()
            val signer = wire.text(input)
            val id = input.readInt()
            val type = input.readInt()
            val physicalAddress = wire.text(input)
            require(wire.validPackage(pkg) && signer.matches(Regex("[0-9a-f]{64}")) && id > 0 && type > 0)
            socket.soTimeout = 0
            val service = NativeAppRoutingService(context)
            val stop = CountDownLatch(1)
            val observer = object : INativeAppRouteObserver.Stub() {
                override fun routeState(phase: String, reason: String, originalDeviceIds: IntArray, actualDeviceIds: IntArray) {
                    runCatching {
                        require(phase in wire.phases)
                        out.writeByte(wire.STATE); wire.text(out, phase); wire.text(out, reason)
                        wire.ids(out, originalDeviceIds); wire.ids(out, actualDeviceIds); out.flush()
                    }
                    if (phase != "verified") runCatching { socket.shutdownInput() }
                }
            }
            val deadline = Handler(service.callbackLooper)
            val expire = Runnable { service.expireDirectLease() }
            deadline.postDelayed(expire, wire.LEASE_MS)
            try {
                service.applyOwned(owner, pkg, uid, signer, id, type, physicalAddress, observer)
                if (input.read() == wire.RESTORE) {
                    service.restoreOwned(owner, observer)
                    require(input.read() == -1)
                }
            } finally {
                deadline.removeCallbacks(expire)
                service.shutdown(stop::countDown)
                stop.await(3, TimeUnit.SECONDS)
            }
        }
    }

    private fun ownerContext(owner: Int): Context {
        // Standalone app_process has no Application. Bootstrap framework access as the real process
        // UID and resolve only the signed app belonging to our kernel-authenticated socket peer.
        val framework = Class.forName("android.app.ActivityThread")
        val thread = framework.getMethod("systemMain").invoke(null)
        val system = framework.getMethod("getSystemContext").invoke(thread) as Context
        val user = UserHandle.getUserHandleForUid(owner)
        val ctx = runCatching {
            Context::class.java.getMethod("createPackageContextAsUser", String::class.java, Int::class.javaPrimitiveType, UserHandle::class.java)
                .invoke(system, NativeAppRoutingPipe.OWNER, 0, user) as Context
        }.getOrElse { system.createPackageContext(NativeAppRoutingPipe.OWNER, 0) }
        val pm = ctx.packageManager
        require(pm.getPackageUid(NativeAppRoutingPipe.OWNER, 0) == owner && pm.getPackagesForUid(owner)?.singleOrNull() == NativeAppRoutingPipe.OWNER)
        return ctx
    }
}
