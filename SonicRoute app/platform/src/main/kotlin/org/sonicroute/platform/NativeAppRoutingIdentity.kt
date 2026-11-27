package org.sonicroute.platform

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import org.sonicroute.core.NativeAppRoutingPipe
import org.sonicroute.core.NativeAppRoutingPipe.hex
import java.io.File
import java.security.MessageDigest

internal object NativeAppRoutingIdentity {
    fun apk(ctx: Context): String {
        val path = ctx.packageManager.getApplicationInfo(NativeAppRoutingPipe.OWNER, 0).sourceDir
        require(NativeAppRoutingPipe.validApk(path, File(path).canonicalPath))
        return path
    }
    fun signers(ctx: Context, apk: String): ByteArray {
        val pm = ctx.packageManager
        val installed = pm.getPackageInfo(NativeAppRoutingPipe.OWNER, PackageManager.GET_SIGNING_CERTIFICATES)
        val archive = requireNotNull(pm.getPackageArchiveInfo(apk, PackageManager.GET_SIGNING_CERTIFICATES))
        require(archive.packageName == NativeAppRoutingPipe.OWNER)
        val certs = certificateSet(installed)
        require(MessageDigest.isEqual(certs, certificateSet(archive)))
        return certs
    }
    fun packageSigner(ctx: Context, name: String): String =
        certificateSet(ctx.packageManager.getPackageInfo(name, PackageManager.GET_SIGNING_CERTIFICATES)).hex()
    private fun certificateSet(info: PackageInfo): ByteArray {
        val signatures = requireNotNull(info.signingInfo?.apkContentsSigners)
        require(signatures.isNotEmpty())
        val fingerprints = signatures.map { MessageDigest.getInstance("SHA-256").digest(it.toByteArray()).hex() }.sorted()
        return MessageDigest.getInstance("SHA-256").digest(fingerprints.joinToString().toByteArray(Charsets.US_ASCII))
    }
}
