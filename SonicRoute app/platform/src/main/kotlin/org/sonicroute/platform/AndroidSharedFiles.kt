package org.sonicroute.platform

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.DocumentsContract
import android.provider.DocumentsContract.Document
import org.json.JSONObject
import org.sonicroute.core.ProtocolError
import org.sonicroute.core.SharedDocument
import org.sonicroute.core.SharedFileFailure
import org.sonicroute.core.SharedFileInput
import org.sonicroute.core.SharedFileNames
import org.sonicroute.core.SharedFileOutput
import org.sonicroute.core.SharedFileStore
import org.sonicroute.core.TrustedPeer
import java.io.Closeable
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

data class FileOfferPrompt(val id: String, val peer: String, val fingerprint: String, val folder: String, val name: String, val size: Long)
data class LocalFileSharing(val folder: String? = null, val writable: Boolean = false, val offer: FileOfferPrompt? = null,
                            val selectionFailed: Boolean = false, val hasSavedSelection: Boolean = false)

internal class AndroidSharedFiles(private val context: Context, private val changed: () -> Unit) : SharedFileStore {
    private val resolver = context.contentResolver
    private val prefs = context.getSharedPreferences("shared_saf_tree", Context.MODE_PRIVATE)
    private val journal = context.getSharedPreferences("shared_saf_pending", Context.MODE_PRIVATE)
    private val grantLock = Any()
    private val renameLock = Any()
    private val writers = mutableMapOf<Uri, Int>()
    private val deferredRelease = mutableSetOf<Uri>()
    private val main = Handler(Looper.getMainLooper())
    private val settingsWorker = Executors.newSingleThreadExecutor { r -> Thread(r, "SonicRoute-file-settings").apply { isDaemon = true } }
    private var choice = 0L
    private data class Tree(val uri: Uri, val generation: Long, val name: String, val writable: Boolean)
    @Volatile private var tree: Tree? = null
    override val grant: Long get() = tree?.generation ?: -1
    var state = LocalFileSharing()
        private set
    private var visible = false
    private var response: ((Boolean) -> Unit)? = null
    private var stagingChecked = false
    private val staging = File(context.cacheDir, "srap-incoming")

    fun restore() {
        if (requestedUri != null) return
        val current = tree
        if (current != null) {
            val permission = try {
                resolver.persistedUriPermissions.find { it.uri == current.uri }
            } catch (_: RuntimeException) {
                suspendSelection()
                return
            }
            if (permission?.isReadPermission != true) clear()
            else if (current.writable && !permission.isWritePermission) {
                suspendSelection()
                select(current.uri, 0, restore = true)
            }
            return
        }
        val text = prefs.getString("tree", null) ?: return
        select(Uri.parse(text), 0, restore = true)
    }

    private fun suspendSelection() {
        tree = null
        state.offer?.let { decide(it.id, false) }
        state = LocalFileSharing(selectionFailed = true, hasSavedSelection = true)
        changed()
    }

    fun setVisible(value: Boolean, keepOffer: Boolean = false) {
        visible = value
        if (!value && !keepOffer) state.offer?.let { decide(it.id, false) }
    }

    fun select(uri: Uri, flags: Int, restore: Boolean = false) {
        val sequence = ++choice
        val previous = tree?.uri ?: prefs.getString("tree", null)?.let(Uri::parse)
        requestedUri = uri
        settingsWorker.execute {
            var forgetSavedSelection = false
            val selected = runCatching {
                if (uri.scheme != "content" || !DocumentsContract.isTreeUri(uri)) {
                    forgetSavedSelection = true
                    return@runCatching null
                }
                if (!restore) {
                    val access = flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
                    require(access and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
                    resolver.takePersistableUriPermission(uri, access)
                }
                val permission = resolver.persistedUriPermissions.firstOrNull { it.uri == uri && it.isReadPermission }
                if (permission == null) {
                    forgetSavedSelection = true
                    return@runCatching null
                }
                val root = readOne(root(uri))
                require(root.directory)
                val provider = context.packageManager.resolveContentProvider(uri.authority.orEmpty(), 0)
                val transactional = uri.authority == "com.android.externalstorage.documents" &&
                    provider?.packageName == "com.android.externalstorage" &&
                    provider.applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_SYSTEM != 0
                if (transactional) recoverStaging(uri)
                Tree(uri, sequence, root.name, transactional && permission.isWritePermission && root.flags and Document.FLAG_DIR_SUPPORTS_CREATE != 0)
            }.getOrNull()
            main.post {
                if (sequence != choice) {
                    if (uri != tree?.uri && uri != requestedUri && uri.toString() != prefs.getString("tree", null)) dropGrant(uri)
                    return@post
                }
                requestedUri = null
                if (selected == null) {
                    if (restore && forgetSavedSelection) prefs.edit().remove("tree").apply()
                    if (!restore && uri != previous) dropGrant(uri)
                    state = state.copy(selectionFailed = true, hasSavedSelection = prefs.contains("tree"))
                } else {
                    state.offer?.let { decide(it.id, false) }
                    tree = selected
                    if (!restore) prefs.edit().putString("tree", uri.toString()).apply()
                    state = LocalFileSharing(selected.name, selected.writable, hasSavedSelection = true)
                    if (previous != null && previous != uri) dropGrant(previous)
                }
                changed()
            }
        }
    }
    private var requestedUri: Uri? = null

    fun clear() {
        choice++
        requestedUri = null
        val old = tree?.uri ?: prefs.getString("tree", null)?.let(Uri::parse)
        tree = null
        state.offer?.let { decide(it.id, false) }
        state = LocalFileSharing()
        prefs.edit().remove("tree").apply()
        changed()
        old?.let(::dropGrant)
    }

    private fun dropGrant(uri: Uri) {
        synchronized(grantLock) {
            if ((writers[uri] ?: 0) > 0) { deferredRelease += uri; return }
        }
        runCatching {
            val grant = resolver.persistedUriPermissions.firstOrNull { it.uri == uri } ?: return
            val flags = (if (grant.isReadPermission) Intent.FLAG_GRANT_READ_URI_PERMISSION else 0) or
                (if (grant.isWritePermission) Intent.FLAG_GRANT_WRITE_URI_PERMISSION else 0)
            resolver.releasePersistableUriPermission(uri, flags)
        }
    }
    override fun permissionLost() { main.post(::restore) }
    override fun isGranted(epoch: Long): Boolean {
        val current = tree?.takeIf { it.generation == epoch } ?: return false
        val present = runCatching { resolver.persistedUriPermissions.any { it.uri == current.uri && it.isReadPermission &&
            (!current.writable || it.isWritePermission) } }.getOrDefault(false)
        if (!present) permissionLost()
        return present
    }

    override fun confirm(peer: TrustedPeer, parent: List<String>, name: String, size: Long, done: (Boolean) -> Unit): AutoCloseable {
        val id = UUID.randomUUID().toString()
        val closed = AtomicBoolean(false)
        main.post {
            val current = tree
            if (closed.get() || !visible || current?.writable != true || response != null) { done(false); return@post }
            response = done
            state = state.copy(offer = FileOfferPrompt(id, peer.displayName, peer.fingerprintSha256,
                (listOf(current.name) + parent).joinToString(" / "), name, size))
            changed()
        }
        return AutoCloseable { closed.set(true); main.post { if (state.offer?.id == id) decide(id, false) } }
    }
    fun decide(id: String, allow: Boolean) {
        if (state.offer?.id != id) return
        val answer = response
        response = null
        state = state.copy(offer = null)
        changed()
        answer?.invoke(allow && visible && state.writable)
    }

    private data class Node(val id: String, val name: String, val directory: Boolean, val size: Long?, val modified: Long, val flags: Int)
    private val projection = arrayOf(Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME, Document.COLUMN_MIME_TYPE,
        Document.COLUMN_SIZE, Document.COLUMN_LAST_MODIFIED, Document.COLUMN_FLAGS)
    private fun android.database.Cursor.node() = Node(getString(0), getString(1).orEmpty(), getString(2) == Document.MIME_TYPE_DIR,
        if (isNull(3)) null else getLong(3).takeIf { it >= 0 }, if (isNull(4)) 0 else getLong(4).coerceAtLeast(0), getInt(5))
    private fun root(uri: Uri) = DocumentsContract.buildDocumentUriUsingTree(uri, DocumentsContract.getTreeDocumentId(uri))
    private fun readOne(uri: Uri): Node = resolver.query(uri, projection, null, null, null)?.use {
        if (it.moveToFirst()) it.node() else null
    } ?: fail(ProtocolError.TARGET_UNAVAILABLE)
    private fun checked(epoch: Long) = tree?.takeIf { it.generation == epoch } ?: fail(ProtocolError.PERMISSION_LOST)
    private fun children(tree: Uri, folder: Uri): List<Node> {
        val uri = DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getDocumentId(folder))
        val result = mutableListOf<Node>()
        resolver.query(uri, projection, null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) {
                if (result.size >= 4096) fail(ProtocolError.UNSUPPORTED, "folder_too_large")
                result += cursor.node()
            }
            if (cursor.extras.getBoolean(DocumentsContract.EXTRA_LOADING, false)) fail(ProtocolError.BUSY, "provider_loading")
        } ?: fail(ProtocolError.IO_FAILURE)
        return result
    }
    private fun resolve(epoch: Long, parent: List<String>): Pair<Tree, Uri> {
        val current = checked(epoch)
        var folder = root(current.uri)
        for (part in parent) {
            val child = children(current.uri, folder).filter { it.name == part }.singleOrNull()
                ?.takeIf { it.directory } ?: fail(ProtocolError.TARGET_UNAVAILABLE)
            folder = DocumentsContract.buildDocumentUriUsingTree(current.uri, child.id)
        }
        checked(epoch)
        return current to folder
    }
    override fun list(grant: Long, parent: List<String>): List<SharedDocument> {
        val (tree, folder) = resolve(grant, parent)
        val rows = children(tree.uri, folder).filter { SharedFileNames.valid(it.name) && !it.name.startsWith(STAGING_PREFIX) }
        if (rows.map { it.name }.distinct().size != rows.size) fail(ProtocolError.IDENTITY_CONFLICT, "ambiguous_document_names")
        checked(grant)
        return rows.map { SharedDocument(it.name, it.directory, it.size, it.modified) }
    }
    override fun open(grant: Long, parent: List<String>, name: String): SharedFileInput {
        val (tree, folder) = resolve(grant, parent)
        val node = children(tree.uri, folder).filter { it.name == name }.singleOrNull()
            ?.takeUnless { it.directory || it.name.startsWith(STAGING_PREFIX) } ?: fail(ProtocolError.TARGET_UNAVAILABLE)
        val size = node.size ?: fail(ProtocolError.UNSUPPORTED, "file_size_unknown")
        val uri = DocumentsContract.buildDocumentUriUsingTree(tree.uri, node.id)
        checked(grant)
        return SharedFileInput(size, resolver.openInputStream(uri) ?: fail(ProtocolError.IO_FAILURE))
    }

    @Synchronized private fun temporary(): File {
        if (!staging.isDirectory && !staging.mkdirs()) fail(ProtocolError.IO_FAILURE)
        if (!stagingChecked) {
            staging.listFiles()?.filter { it.isFile && it.name.matches(Regex("upload[0-9]+\\.part")) }?.forEach { it.delete() }
            stagingChecked = true
        }
        return File.createTempFile("upload", ".part", staging)
    }

    override fun stage(grant: Long, parent: List<String>, name: String): SharedFileOutput {
        val (tree, folder) = resolve(grant, parent)
        if (!tree.writable || readOne(folder).flags and Document.FLAG_DIR_SUPPORTS_CREATE == 0) fail(ProtocolError.PERMISSION_REQUIRED)
        if (children(tree.uri, folder).any { it.name.equals(name, true) }) fail(ProtocolError.BUSY, "name_exists")
        if (name.startsWith(STAGING_PREFIX)) fail(ProtocolError.INVALID_ARGUMENT)
        val temp = temporary()
        val output = try { temp.outputStream() } catch (e: Exception) { temp.delete(); throw e }
        return object : SharedFileOutput {
            override val stream = output
            private val openLock = Any()
            private var providerStream: Closeable? = null
            private var closed = false
            private fun <T : Closeable, R> useProvider(resource: T?, operation: (T) -> R): R {
                val opened = resource ?: fail(ProtocolError.IO_FAILURE)
                return opened.use {
                    synchronized(openLock) {
                        if (closed) fail(ProtocolError.CANCELED)
                        providerStream = opened
                    }
                    try { operation(opened) } finally { synchronized(openLock) { providerStream = null } }
                }
            }
            override fun commit(size: Long, sha256: String, valid: () -> Boolean): String {
                stream.close()
                if (!valid() || temp.length() != size) fail(ProtocolError.CANCELED)
                synchronized(grantLock) {
                    checked(grant)
                    writers[tree.uri] = (writers[tree.uri] ?: 0) + 1
                }
                val marker = STAGING_PREFIX + UUID.randomUUID() + ".part"
                val record = JSONObject().put("tree", tree.uri.toString()).put("parent", DocumentsContract.getDocumentId(folder))
                var recorded = false
                var verifiedFinal: Node? = null
                var committed = false
                try {
                    val existing = children(tree.uri, folder)
                    if (existing.any { it.name.equals(name, true) || it.name == marker }) fail(ProtocolError.BUSY, "name_exists")
                    if (!journal.edit().putString(marker, record.toString()).commit()) fail(ProtocolError.IO_FAILURE, "cannot_record_staging")
                    recorded = true
                    val owned = DocumentsContract.createDocument(resolver, folder, "application/octet-stream", marker)
                        ?: fail(ProtocolError.IO_FAILURE, "provider_create_failed")
                    if (owned.authority != tree.uri.authority) fail(ProtocolError.IDENTITY_CONFLICT)
                    val actual = readOne(owned)
                    if (existing.any { it.id == actual.id } || actual.name != marker || actual.directory ||
                        children(tree.uri, folder).singleOrNull { it.id == actual.id && it.name == marker } == null) {
                        fail(ProtocolError.IDENTITY_CONFLICT)
                    }
                    record.put("id", actual.id)
                    if (!journal.edit().putString(marker, record.toString()).commit()) fail(ProtocolError.IO_FAILURE, "cannot_record_staging")
                    val flags = Document.FLAG_SUPPORTS_RENAME or Document.FLAG_SUPPORTS_DELETE or Document.FLAG_SUPPORTS_WRITE
                    if (actual.flags and flags != flags) fail(ProtocolError.UNSUPPORTED, "provider_commit_unsupported")
                    temp.inputStream().use { source ->
                        useProvider(resolver.openOutputStream(owned, "wt")) { target ->
                            copy(source, valid) { buffer, n -> target.write(buffer, 0, n) }
                        }
                    }
                    val digest = MessageDigest.getInstance("SHA-256")
                    val written = useProvider(resolver.openInputStream(owned)) { source -> copy(source, valid) { bytes, n -> digest.update(bytes, 0, n) } }
                    val actualHash = digest.digest().joinToString("") { "%02X".format(java.util.Locale.ROOT, it) }
                    if (written != size || actualHash != sha256) fail(ProtocolError.IO_FAILURE, "provider_readback_mismatch")
                    val saved = synchronized(renameLock) {
                        if (!valid() || !isGranted(grant)) fail(ProtocolError.CANCELED)
                        val before = children(tree.uri, folder)
                        if (before.any { it.name.equals(name, true) }) fail(ProtocolError.BUSY, "name_exists")
                        if (before.singleOrNull { it.name == marker }?.id != actual.id) fail(ProtocolError.IDENTITY_CONFLICT)
                        record.put("renameTo", name)
                        if (!journal.edit().putString(marker, record.toString()).commit()) fail(ProtocolError.IO_FAILURE, "cannot_record_staging")
                        val finished = DocumentsContract.renameDocument(resolver, owned, name) ?: fail(ProtocolError.IO_FAILURE, "provider_rename_failed")
                        if (finished.authority != tree.uri.authority) fail(ProtocolError.IDENTITY_CONFLICT)
                        val id = DocumentsContract.getDocumentId(finished)
                        val after = children(tree.uri, folder)
                        if (before.any { it.id == id && it.id != actual.id } || after.any { it.name == marker }) fail(ProtocolError.IDENTITY_CONFLICT)
                        val entry = after.singleOrNull { it.id == id && it.name == name && !it.directory }
                            ?: fail(ProtocolError.IDENTITY_CONFLICT)
                        verifiedFinal = entry
                        entry
                    }
                    if (saved.size != size) fail(ProtocolError.IO_FAILURE, "provider_size_mismatch")
                    if (!valid() || !isGranted(grant)) fail(ProtocolError.CANCELED, "grant_changed")
                    committed = true
                    return saved.name
                } finally {
                    val final = verifiedFinal
                    val clean = committed || !recorded || runCatching {
                        if (final != null) deleteOwned(tree.uri, folder, final.name, final.id)
                        else cleanMarker(tree.uri, folder, marker, record)
                    }.getOrDefault(false)
                    if (recorded && clean) journal.edit().remove(marker).apply()
                    val drop = synchronized(grantLock) {
                        val count = writers.getValue(tree.uri) - 1
                        if (count > 0) { writers[tree.uri] = count; false }
                        else { writers.remove(tree.uri); deferredRelease.remove(tree.uri) }
                    }
                    if (drop) main.post {
                        if (this@AndroidSharedFiles.tree?.uri != tree.uri && requestedUri != tree.uri &&
                            prefs.getString("tree", null) != tree.uri.toString()) dropGrant(tree.uri)
                    }
                    if (!clean) fail(ProtocolError.IO_FAILURE, "provider_cleanup_failed")
                }
            }
            override fun close() {
                val resource = synchronized(openLock) { closed = true; providerStream }
                try { runCatching { resource?.close() }; stream.close() } finally { temp.delete() }
            }
        }
    }
    private fun deleteOwned(tree: Uri, folder: Uri, name: String, id: String): Boolean {
        val rows = children(tree, folder).filter { it.name == name }
        if (rows.isEmpty()) return true
        if (rows.singleOrNull()?.let { it.id == id && !it.directory } != true) return false
        return DocumentsContract.deleteDocument(resolver, DocumentsContract.buildDocumentUriUsingTree(tree, id))
    }
    private fun cleanMarker(tree: Uri, folder: Uri, marker: String, record: JSONObject): Boolean {
        val rows = children(tree, folder)
        val found = rows.filter { it.name == marker }
        if (found.isEmpty()) {
            val uncertain = record.optString("renameTo")
            return uncertain.isEmpty() || rows.none { it.name == uncertain }
        }
        val node = found.singleOrNull()?.takeUnless { it.directory } ?: return false
        if (record.has("id") && node.id != record.getString("id")) return false
        return deleteOwned(tree, folder, marker, node.id)
    }
    private fun recoverStaging(uri: Uri) {
        synchronized(grantLock) { if ((writers[uri] ?: 0) > 0) return }
        for ((marker, value) in journal.all) {
            if (!marker.matches(Regex("\\.sonicroute-[a-f0-9-]{36}\\.part"))) continue
            runCatching {
                val record = JSONObject(value as String)
                if (record.getString("tree") != uri.toString()) return@runCatching
                val folder = DocumentsContract.buildDocumentUriUsingTree(uri, record.getString("parent"))
                if (cleanMarker(uri, folder, marker, record)) {
                    journal.edit().remove(marker).apply()
                }
            }
        }
    }
    private fun copy(source: java.io.InputStream, valid: () -> Boolean, consume: (ByteArray, Int) -> Unit): Long {
        val buffer = ByteArray(32768)
        var total = 0L
        while (true) {
            if (!valid()) fail(ProtocolError.CANCELED)
            val n = source.read(buffer)
            if (n < 0) return total
            if (n == 0) fail(ProtocolError.IO_FAILURE)
            consume(buffer, n)
            total += n
        }
    }
    private fun fail(code: ProtocolError, reason: String = code.wireId): Nothing = throw SharedFileFailure(code, reason)
    private companion object { const val STAGING_PREFIX = ".sonicroute-" }
}
