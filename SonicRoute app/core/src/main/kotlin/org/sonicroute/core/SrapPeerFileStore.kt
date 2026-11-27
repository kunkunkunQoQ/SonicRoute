package org.sonicroute.core

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.Locale

/** Only the current atomically committed trust list is authoritative. Older backups can predate revocation. */
class SrapPeerFileStore(directory: File) : TrustedPeerRegistry {
    private val primary = File(directory, "srap_trusted_peers.json")
    private var peers: Map<String, TrustedPeer> = load()

    @Synchronized
    override fun findByFingerprint(fingerprintSha256: String) = peers[fingerprintSha256.uppercase(Locale.ROOT)]

    @Synchronized
    override fun listPeers() = peers.values.toList()

    @Synchronized
    override fun savePeer(peer: TrustedPeer) {
        val canonical = peer.copy(fingerprintSha256 = peer.fingerprintSha256.uppercase(Locale.ROOT))
        require(validKey(canonical)) { "invalid_trust_key" }
        commit(peers + (canonical.fingerprintSha256 to canonical))
    }

    @Synchronized
    override fun removePeer(fingerprintSha256: String): Boolean {
        val key = fingerprintSha256.uppercase(Locale.ROOT)
        if (key !in peers) return false
        commit(peers - key)
        return true
    }

    @Synchronized
    override fun touchLastConnected(fingerprintSha256: String, nowMs: Long) {
        val peer = findByFingerprint(fingerprintSha256) ?: return
        commit(peers + (peer.fingerprintSha256 to peer.copy(lastConnectedAtMs = nowMs)))
    }

    private fun load(): Map<String, TrustedPeer> {
        if (!primary.isFile || primary.length() !in 2..MAX_BYTES) return emptyMap()
        return runCatching {
            val root = StrictJson.parseUtf8(primary.readBytes()) as JsonObject
            require(SrapCodec.requiredInt(root, "version") == 1)
            val rows = (root["peers"] as JsonArray).items
            val loaded = LinkedHashMap<String, TrustedPeer>()
            for (row in rows) {
                val obj = row as JsonObject
                val fp = SrapCodec.requiredText(obj, "fingerprintSha256").uppercase(Locale.ROOT)
                val name = SrapCodec.requiredText(obj, "displayName").take(64)
                val key = SrapCodec.requiredText(obj, "publicKeyDerB64", 344)
                val paired = SrapCodec.requiredLong(obj, "pairedAtMs")
                val last = if (obj.has("lastConnectedAtMs")) SrapCodec.requiredLong(obj, "lastConnectedAtMs") else paired
                val peer = TrustedPeer(fp, name, key, paired, last)
                require(validKey(peer) && loaded.put(fp, peer) == null)
            }
            loaded
        }.getOrElse { emptyMap() }
    }

    private fun validKey(peer: TrustedPeer): Boolean {
        val spki = SrapCrypto.base64UrlDecodeNoPad(peer.publicKeyDerB64)
        SrapCrypto.parseEcP256Spki(spki)
        return SrapCrypto.sha256HexUpper(spki) == peer.fingerprintSha256
    }

    private fun commit(next: Map<String, TrustedPeer>) {
        val rows = next.values.map { p -> StrictJson.obj(
            "fingerprintSha256" to StrictJson.str(p.fingerprintSha256),
            "displayName" to StrictJson.str(p.displayName),
            "publicKeyDerB64" to StrictJson.str(p.publicKeyDerB64),
            "pairedAtMs" to StrictJson.num(p.pairedAtMs),
            "lastConnectedAtMs" to StrictJson.num(p.lastConnectedAtMs)
        ) }
        val bytes = StrictJson.encodeUtf8(StrictJson.obj("version" to StrictJson.num(1), "peers" to StrictJson.arr(rows)))
        if (bytes.size > MAX_BYTES) throw IOException("trust_store_full")
        val directory = primary.parentFile ?: throw IOException("trust_store_directory_missing")
        Files.createDirectories(directory.toPath())
        val tmp = Files.createTempFile(directory.toPath(), "srap_trust_", ".tmp")
        try {
            FileOutputStream(tmp.toFile()).use { output ->
                output.write(bytes)
                output.fd.sync()
            }
            Files.move(tmp, primary.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
            peers = next
        } catch (failure: Exception) {
            runCatching { Files.deleteIfExists(tmp) }
            throw failure
        }
    }

    private companion object { const val MAX_BYTES = 512L * 1024 }
}
