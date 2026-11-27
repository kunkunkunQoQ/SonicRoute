package org.sonicroute.app.test

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.os.PersistableBundle
import android.os.SystemClock
import android.view.View
import android.widget.Switch
import org.sonicroute.app.R
import org.sonicroute.platform.SonicRouteRuntime
import java.io.File

/** Instrumentation APK only. A nonempty clipboard is skipped before any read or mutation. */
internal class ClipboardInterop(private val page: Activity, private val runtime: SonicRouteRuntime,
                                run: String, private val peer: String, private val report: (String) -> Unit) {
    private val manager = page.getSystemService(ClipboardManager::class.java)
    private val signal = Regex("interop-$run[.]clip-([a-f0-9]{32})")
    private val directory: File = page.cacheDir
    private val pending = File(directory, "interop-$run.clip-request")
    private val prepare = File(directory, "interop-$run.clip-opt")
    private var enabledForTest = false
    private var token: String? = null
    private var phase = 0
    private var by = 0L
    @Volatile var result: String? = null
        private set
    private val origin = "org.sonicroute.clip.syncOrigin"
    private val testMarker = "org.sonicroute.test.clipboard"

    // Called on the main looper while the dedicated interoperability run is active.
    fun step() {
        if (result != null) return
        if (prepare.isFile && page.hasWindowFocus()) {
            prepare.delete()
            if (manager.hasPrimaryClip() || manager.primaryClipDescription != null) { finish("skipped_nonempty"); return }
            if (!runtime.currentSnapshot().clipboardSharingEnabled) {
                page.findViewById<View>(R.id.nav_settings).performClick()
                val toggle = page.findViewById<Switch>(R.id.clipboard_share)
                check(toggle.isShown && !toggle.isChecked && toggle.performClick())
                check(runtime.currentSnapshot().clipboardSharingEnabled)
                enabledForTest = true
            }
            report("prepared")
        }
        if (token == null) {
            if (!pending.exists()) return
            val marker = directory.listFiles { _, name -> signal.matches(name) }?.singleOrNull() ?: return
            token = signal.matchEntire(marker.name)!!.groupValues[1]
            marker.delete()
            pending.delete()
            by = SystemClock.elapsedRealtime() + 18000
        }
        if (SystemClock.elapsedRealtime() > by) { finish("timeout"); return }
        val snapshot = runtime.currentSnapshot()
        if (!page.hasWindowFocus() || !snapshot.clipboardSharingEnabled ||
            phase != 2 && snapshot.lan.connectedPeers.singleOrNull()?.fingerprintSha256 != peer) return
        when (phase) {
            0 -> {
                if (manager.hasPrimaryClip() || manager.primaryClipDescription != null) { finish("skipped_nonempty"); return }
                // Check again immediately before the first write. ClipboardManager supplies no cross-process CAS.
                if (manager.hasPrimaryClip()) { finish("skipped_nonempty"); return }
                phase = 1; put("ready")
            }
            1 -> {
                when (ownedText()) {
                    mark("pc") -> { put("phone"); phase = 2 }
                    mark("ready") -> Unit
                    else -> finish("replaced_externally")
                }
            }
            2 -> {
                when (ownedText()) {
                    mark("done") -> { clearIfOwned(); check(ownedText() == null); finish("passed") }
                    mark("phone") -> Unit
                    else -> finish("replaced_externally")
                }
            }
        }
    }
    private fun put(direction: String) {
        val clip = ClipData.newPlainText("SonicRoute clipboard check", mark(direction))
        clip.description.extras = PersistableBundle().apply { putString(testMarker, token) }
        manager.setPrimaryClip(clip)
        check(ownedText() == mark(direction))
    }
    private fun ownedText(): String? {
        val description = manager.primaryClipDescription ?: return null
        if (description.label != "SonicRoute clipboard check" && description.label != "SonicRoute") return null
        if (description.extras?.getString(testMarker) != token &&
            (description.label != "SonicRoute" || description.extras?.getString(origin) == null)) return null
        val clip = manager.primaryClip ?: return null
        if (clip.itemCount != 1 || clip.description.timestamp != description.timestamp ||
            clip.description.label != description.label) return null
        val text = clip.getItemAt(0).text as? String ?: return null
        return text.takeIf { it in setOf(mark("ready"), mark("pc"), mark("phone"), mark("done")) }
    }
    private fun mark(direction: String) = "SonicRoute-interop/$direction/$token"
    private fun clearIfOwned() {
        if (token != null && phase > 0 && page.hasWindowFocus() && ownedText() != null) manager.clearPrimaryClip()
    }
    private fun finish(state: String) { clearIfOwned(); result = state; report(state) }
    fun close() {
        clearIfOwned()
        if (enabledForTest) runtime.setClipboardSharingEnabled(false)
        prepare.delete()
        pending.delete()
    }
}
