package org.sonicroute.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.security.MessageDigest
import java.security.SecureRandom

/** Binary messages on a one-use AF_UNIX connection between the phone UI and its own root child. */
object NativeAppRoutingPipe {
    const val OWNER = "org.sonicroute.app"
    const val ENTRY = "org.sonicroute.platform.NativeAppRoutingHelper"
    const val MAGIC = 0x53525231
    const val APPLY = 1
    const val RESTORE = 2
    const val STATE = 3
    const val LEASE_MS = 300000L
    val phases = setOf("verified", "released", "restored", "error")
    private val path = Regex("/data/app/[A-Za-z0-9+~._=/\u002d]+/base[.]apk")
    private val packageName = Regex("[A-Za-z][A-Za-z0-9_]*(?:[.][A-Za-z][A-Za-z0-9_]*)+")
    fun validPackage(name: String) = name.length <= 255 && packageName.matches(name)
    fun validApk(pathName: String, canonical: String) = path.matches(pathName) && pathName == canonical &&
        pathName.split('/').none { it == "." || it == ".." }
    fun rootProof(peerUid: Int, nonce: ByteArray, echo: ByteArray, signer: ByteArray, loadedSigner: ByteArray) =
        peerUid == 0 && nonce.size == 32 && signer.size == 32 && MessageDigest.isEqual(nonce, echo) &&
            MessageDigest.isEqual(signer, loadedSigner)
    fun random() = ByteArray(32).also(SecureRandom()::nextBytes)
    fun ByteArray.hex(): String = joinToString("") { "%02x".format(it) }
    fun text(out: DataOutputStream, value: String) {
        val bytes = value.toByteArray(Charsets.UTF_8)
        require(bytes.size <= 512 && !value.contains('\u0000'))
        out.writeShort(bytes.size); out.write(bytes)
    }
    fun text(input: DataInputStream): String {
        val size = input.readUnsignedShort()
        require(size <= 512)
        val bytes = ByteArray(size).also(input::readFully)
        val value = bytes.toString(Charsets.UTF_8)
        require(value.toByteArray(Charsets.UTF_8).contentEquals(bytes) && !value.contains('\u0000'))
        return value
    }
    fun ids(out: DataOutputStream, list: IntArray) {
        require(list.size <= 32 && list.all { it > 0 })
        out.writeByte(list.size)
        list.forEach(out::writeInt)
    }
    fun ids(input: DataInputStream): IntArray {
        val count = input.readUnsignedByte()
        require(count <= 32)
        return IntArray(count) { input.readInt().also { require(it > 0) } }
    }
}
