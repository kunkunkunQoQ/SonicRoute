package org.sonicroute.app

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import org.sonicroute.platform.SonicRouteRuntime
import org.sonicroute.platform.SonicRouteRuntimeSnapshot

internal fun chooseProjectionPeer(page: Activity, runtime: SonicRouteRuntime, snap: SonicRouteRuntimeSnapshot, title: Int,
    selected: (String, String) -> Unit) {
    val peers = snap.lan.connectedPeers.distinctBy { it.fingerprintSha256 }
    if (peers.isEmpty()) return
    val labels = peers.map { "${it.displayName}\n${it.fingerprintSha256}" }.toTypedArray()
    AlertDialog.Builder(page).setTitle(title).setItems(labels) { _, which ->
        val pin = peers[which].fingerprintSha256
        val sid = runtime.captureSessionForPeer(pin)
        if (sid != null) selected(pin, sid) else Toast.makeText(page, R.string.capture_expired, Toast.LENGTH_LONG).show()
    }.setNegativeButton(android.R.string.cancel, null).show()
}

internal class ScreenSharingPage(private val page: Activity, private val runtime: SonicRouteRuntime, saved: Bundle?) {
    private var peer = saved?.getString("screenPeer")
    private var session = saved?.getString("screenSession")
    private val button = page.findViewById<Button>(R.id.share_screen)
    private val status = page.findViewById<TextView>(R.id.share_screen_status)

    init {
        button.setOnClickListener {
            val current = runtime.currentSnapshot()
            if (current.screenSharing != null) runtime.stopSharingScreen() else chooseProjectionPeer(page, runtime, current, R.string.screen_peer) { pin, sid ->
                peer = pin
                session = sid
                runCatching {
                    page.startActivityForResult(page.getSystemService(MediaProjectionManager::class.java).createScreenCaptureIntent(), PROJECT)
                }.onFailure { clear(); failed() }
            }
        }
    }

    fun render(snap: SonicRouteRuntimeSnapshot) {
        val sharing = snap.screenSharing
        button.setText(if (sharing == null) R.string.share_screen else R.string.screen_stop)
        button.isEnabled = sharing != null || snap.lan.connectedPeers.isNotEmpty() && snap.playbackSharing == null
        status.text = when {
            sharing != null -> page.getString(if (sharing.connected) R.string.screen_sending else R.string.screen_waiting,
                snap.lan.connectedPeers.firstOrNull { it.fingerprintSha256 == sharing.peer }?.displayName ?: sharing.peer.take(12))
            snap.playbackSharing != null -> page.getString(R.string.screen_stop_playback)
            snap.lan.connectedPeers.isEmpty() -> page.getString(R.string.screen_connect)
            else -> page.getString(R.string.screen_guide)
        }
    }

    @Suppress("DEPRECATION")
    fun activityResult(code: Int, result: Int, data: Intent?) {
        if (code != PROJECT) return
        val pin = peer
        val sid = session
        clear()
        if (result != Activity.RESULT_OK || data == null) return
        val (w, h) = if (Build.VERSION.SDK_INT >= 30) page.windowManager.maximumWindowMetrics.bounds.let { it.width() to it.height() }
            else DisplayMetrics().apply { page.windowManager.defaultDisplay.getRealMetrics(this) }.let { it.widthPixels to it.heightPixels }
        if (pin == null || sid == null || !runtime.shareScreen(sid, pin, result, data, w, h, page.resources.displayMetrics.densityDpi)) failed()
    }

    fun save(bundle: Bundle) { bundle.putString("screenPeer", peer); bundle.putString("screenSession", session) }
    private fun clear() { peer = null; session = null }
    private fun failed() { Toast.makeText(page, R.string.screen_failed, Toast.LENGTH_LONG).show() }
    private companion object { const val PROJECT = 1403 }
}
