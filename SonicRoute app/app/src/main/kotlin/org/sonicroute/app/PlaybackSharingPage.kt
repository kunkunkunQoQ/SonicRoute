package org.sonicroute.app

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import org.sonicroute.platform.SonicRouteRuntime
import org.sonicroute.platform.SonicRouteRuntimeSnapshot
import org.sonicroute.platform.PlaybackCaptureSource
import org.sonicroute.platform.LauncherCatalogState

internal class PlaybackSharingPage(private val page: Activity, private val runtime: SonicRouteRuntime, saved: Bundle?) {
    private val button = page.findViewById<Button>(R.id.capture_playback)
    private val status = page.findViewById<TextView>(R.id.capture_playback_status)
    private var peer = saved?.getString("capturePeer")
    private var session = saved?.getString("captureSession")
    private var picker: AlertDialog? = null
    private var waitingForApps: Pair<String, String>? = null
    private var source = saved?.getString("captureSigner")?.let { signer -> saved.getString("captureApp")?.let { app ->
        PlaybackCaptureSource(app, saved.getInt("captureUid", -1), signer, saved.getLong("captureUpdatedAt", -1))
    } }

    init {
        button.setOnClickListener {
            val snap = runtime.currentSnapshot()
            if (snap.playbackSharing != null) runtime.stopSharingPlayback() else selectPeer(snap)
        }
    }

    fun render(snap: SonicRouteRuntimeSnapshot) {
        val sharing = snap.playbackSharing
        button.setText(if (sharing != null) R.string.capture_stop else R.string.capture_title)
        button.isEnabled = sharing != null || snap.screenSharing == null && snap.lan.connectedPeers.isNotEmpty()
        status.text = when {
            sharing != null -> page.getString(if (sharing.recording) R.string.capture_active else R.string.capture_waiting,
                snap.audio.appCatalog.entries.find { it.packageName == sharing.app }?.label ?: sharing.app,
                snap.lan.connectedPeers.find { it.fingerprintSha256 == sharing.peer }?.displayName.orEmpty())
            snap.screenSharing != null -> page.getString(R.string.capture_stop_screen)
            snap.lan.connectedPeers.isEmpty() -> page.getString(R.string.capture_connect)
            else -> page.getString(R.string.capture_description)
        }
        val pending = waitingForApps ?: return
        if (runtime.captureSessionForPeer(pending.first) != pending.second) {
            dismissPicker()
        } else if (snap.audio.catalogState == LauncherCatalogState.READY || snap.audio.catalogState == LauncherCatalogState.FAILED) {
            dismissPicker()
            showApps(snap, pending.first, pending.second)
        }
    }

    private fun selectPeer(snap: SonicRouteRuntimeSnapshot) {
        chooseProjectionPeer(page, runtime, snap, R.string.capture_peer) { pin, sid ->
            dismissPicker()
            runtime.retryAppCatalogIfFailed()
            val latest = runtime.currentSnapshot()
            if (latest.audio.catalogState == LauncherCatalogState.READY || latest.audio.catalogState == LauncherCatalogState.FAILED) {
                showApps(latest, pin, sid)
            } else {
                waitingForApps = pin to sid
                picker = AlertDialog.Builder(page).setTitle(R.string.capture_app).setMessage(R.string.app_catalog_loading)
                    .setNegativeButton(android.R.string.cancel, null).create().also { dialog ->
                        dialog.setOnDismissListener { if (picker === dialog) { waitingForApps = null; picker = null } }
                        dialog.show()
                    }
            }
        }
    }

    private fun showApps(snap: SonicRouteRuntimeSnapshot, pin: String, sid: String) {
        val apps = snap.audio.appCatalog.entries.filter { !it.isSelf && it.uid % 100000 >= 10000 }
        val builder = AlertDialog.Builder(page).setTitle(R.string.capture_app)
        if (apps.isEmpty()) {
            builder.setMessage(if (snap.audio.catalogState == LauncherCatalogState.FAILED) R.string.app_catalog_failed else R.string.app_catalog_empty)
        } else {
            builder
                .setItems(apps.map { "${it.label}\n${it.packageName}" }.toTypedArray()) { _, index ->
                    val chosen = apps[index]
                    if (runtime.captureSessionForPeer(pin) != sid) { clear(); show(R.string.capture_expired); return@setItems }
                    source = runtime.captureSource(chosen.packageName, chosen.uid)
                    if (source == null) { clear(); show(R.string.capture_refused); return@setItems }
                    peer = pin; session = sid
                    if (page.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) project()
                    else page.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), RECORD)
                }
        }
        picker = builder.setNegativeButton(android.R.string.cancel, null).show()
    }

    fun dismissPicker() {
        waitingForApps = null
        val current = picker
        picker = null
        current?.dismiss()
    }

    private fun project() {
        if (session == null || peer?.let(runtime::captureSessionForPeer) != session) { clear(); show(R.string.capture_expired); return }
        runCatching {
            val manager = page.getSystemService(MediaProjectionManager::class.java)
            page.startActivityForResult(manager.createScreenCaptureIntent(), PROJECT)
        }.onFailure { clear(); show(R.string.capture_refused) }
    }

    fun permissionResult(code: Int, granted: Boolean) {
        if (code != RECORD) return
        if (granted) project() else { clear(); show(R.string.capture_permission) }
    }

    fun activityResult(code: Int, result: Int, data: Intent?) {
        if (code != PROJECT) return
        val selected = source; val pin = peer; val sid = session
        clear()
        if (result == Activity.RESULT_OK && data != null &&
            (selected == null || pin == null || sid == null || !runtime.sharePlayback(selected, pin, sid, result, data))) show(R.string.capture_refused)
    }

    fun save(bundle: Bundle) {
        source?.let {
            bundle.putString("captureApp", it.app); bundle.putInt("captureUid", it.uid)
            bundle.putString("captureSigner", it.signer); bundle.putLong("captureUpdatedAt", it.updatedAt)
        }
        bundle.putString("capturePeer", peer); bundle.putString("captureSession", session)
    }
    private fun clear() { source = null; peer = null; session = null }
    private fun show(id: Int) { Toast.makeText(page, id, Toast.LENGTH_LONG).show() }
    private companion object { const val RECORD = 1401; const val PROJECT = 1402 }
}
