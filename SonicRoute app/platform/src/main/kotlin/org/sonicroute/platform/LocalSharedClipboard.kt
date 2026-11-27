package org.sonicroute.platform

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.PersistableBundle
import org.sonicroute.core.OperationResult
import org.sonicroute.core.ProtocolError
import org.sonicroute.core.SrapClipboardText
import org.sonicroute.core.SrapClipboardValue
import java.util.UUID

/** Main-looper owner. Android clipboard access is limited to locally enabled, focused, authenticated watches. */
class LocalSharedClipboard(context: Context, private val hasFocus: () -> Boolean) {
    private val clipboard = context.applicationContext.getSystemService(ClipboardManager::class.java)
    private data class Watch(val id: String, val changed: () -> Unit)
    private val watches = LinkedHashMap<String, Watch>()
    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        if (available && hasFocus() && watches.isNotEmpty()) {
            val description = runCatching { clipboard?.primaryClipDescription }.getOrNull()
            if (lastWriteMarker != null && description?.timestamp == lastStamp &&
                description?.extras?.getString(SYNC_ORIGIN) == lastWriteMarker) return@OnPrimaryClipChangedListener
            lastWriteMarker = null
            revision++
            lastStamp = description?.timestamp
            signal()
        }
    }
    var available = false
        private set
    private var revision = 0L
    private var lastStamp: Long? = null
    private var lastWriteMarker: String? = null

    fun setAvailable(value: Boolean): Boolean {
        val next = value && clipboard != null
        if (available == next) return false
        available = next
        revision++
        if (!next) {
            if (watches.isNotEmpty()) runCatching { clipboard?.removePrimaryClipChangedListener(listener) }
            watches.clear()
            lastStamp = null
            lastWriteMarker = null
        }
        return true
    }

    fun watch(session: String, id: String, changed: () -> Unit): OperationResult<Long> {
        val manager = clipboard ?: return denied()
        if (!available || !hasFocus()) return denied()
        if (watches.containsKey(session)) return OperationResult.Failure(ProtocolError.BUSY)
        if (watches.isEmpty()) {
            try {
                manager.addPrimaryClipChangedListener(listener)
                lastStamp = manager.primaryClipDescription?.timestamp
                revision++
            } catch (_: RuntimeException) {
                runCatching { manager.removePrimaryClipChangedListener(listener) }
                return denied()
            }
        }
        watches[session] = Watch(id, changed)
        return OperationResult.Done(revision)
    }

    fun unwatch(session: String, id: String) {
        if (watches[session]?.id != id) return
        watches.remove(session)
        if (watches.isEmpty()) {
            runCatching { clipboard?.removePrimaryClipChangedListener(listener) }
            lastStamp = null
            lastWriteMarker = null
        }
    }

    private fun observeStamp(desc: ClipDescription?) {
        if (desc?.timestamp == lastStamp) return
        lastStamp = desc?.timestamp
        revision++
        signal()
    }
    private fun signal() { watches.values.toList().forEach { it.changed() } }
    private fun denied() = OperationResult.Failure(ProtocolError.PERMISSION_LOST, "focused_clipboard_required")

    fun read(session: String, id: String): OperationResult<SrapClipboardValue> {
        val manager = clipboard ?: return denied()
        if (!available || !hasFocus() || watches[session]?.id != id) return denied()
        return try {
            val description = manager.primaryClipDescription
            observeStamp(description)
            // Metadata is sufficient to filter sensitive and non-text entries before asking for their payload.
            val text = if (shareable(description)) {
                val clip = manager.primaryClip
                if (clip != null && shareable(clip.description) && clip.description.timestamp == description?.timestamp) plainText(clip) else null
            } else null
            OperationResult.Done(SrapClipboardValue(revision, text))
        } catch (_: RuntimeException) { denied() }
    }

    fun write(session: String, id: String, expected: Long, text: String): OperationResult<Long> {
        val manager = clipboard ?: return denied()
        if (!available || !hasFocus() || watches[session]?.id != id) return denied()
        if (SrapClipboardText.encode(text) == null) return OperationResult.Failure(ProtocolError.INVALID_ARGUMENT)
        return try {
            observeStamp(manager.primaryClipDescription)
            if (revision != expected) return OperationResult.Failure(ProtocolError.STALE_SESSION, "clipboard_changed")
            val marker = UUID.randomUUID().toString()
            val clip = ClipData.newPlainText("SonicRoute", text)
            clip.description.extras = PersistableBundle().apply { putString(SYNC_ORIGIN, marker) }
            manager.setPrimaryClip(clip)
            val actual = manager.primaryClip
            if (actual == null || actual.description.extras?.getString(SYNC_ORIGIN) != marker || plainText(actual) != text) {
                return OperationResult.Failure(ProtocolError.IO_FAILURE, "clipboard_readback_failed")
            }
            lastStamp = actual.description.timestamp
            lastWriteMarker = marker
            revision++
            signal()
            OperationResult.Done(revision)
        } catch (_: RuntimeException) { denied() }
    }

    private fun shareable(desc: ClipDescription?): Boolean = desc != null && desc.mimeTypeCount == 1 &&
        desc.getMimeType(0) == ClipDescription.MIMETYPE_TEXT_PLAIN &&
        desc.extras?.getBoolean("android.content.extra.IS_SENSITIVE", false) != true &&
        desc.extras?.containsKey("org.sonicroute.clip.owner") != true && desc.extras?.containsKey(SYNC_ORIGIN) != true

    private fun plainText(clip: ClipData): String? {
        if (clip.itemCount != 1) return null
        val item = clip.getItemAt(0)
        if (item.uri != null || item.intent != null || item.htmlText != null) return null
        val text = item.text as? String ?: return null
        return text.takeIf { SrapClipboardText.encode(it) != null }
    }

    companion object { private const val SYNC_ORIGIN = "org.sonicroute.clip.syncOrigin" }
}
