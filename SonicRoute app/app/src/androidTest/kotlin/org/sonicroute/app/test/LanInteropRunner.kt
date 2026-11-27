package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import android.os.FileObserver
import android.os.SystemClock
import android.util.Base64
import android.view.View
import org.sonicroute.app.MainActivity
import org.sonicroute.app.R
import org.sonicroute.platform.SonicRouteRuntime
import java.io.File
import java.security.KeyFactory
import java.security.spec.MGF1ParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.concurrent.FutureTask
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.spec.OAEPParameterSpec
import javax.crypto.spec.PSource

/** Test APK only. The host retains the private key used to unwrap the single-use invitation. */
class LanInteropRunner : Instrumentation() {
    private lateinit var args: Bundle
    private var eventFile: File? = null

    override fun onCreate(arguments: Bundle) {
        super.onCreate(arguments)
        args = arguments
        start()
    }

    private fun <T> ui(action: () -> T): T {
        val work = FutureTask(action)
        runOnMainSync(work)
        return work.get()
    }

    private fun status(name: String, value: String) {
        eventFile?.appendText("INSTRUMENTATION_STATUS: $name=$value\n")
        sendStatus(1, Bundle().apply { putString(name, value) })
    }

    override fun onStart() {
        if (args.getString("mode") == "business-ui") {
            BusinessUiInterop(this, args.getString("evidence")).run()
            return
        }
        if (args.getString("mode") == "owned-focus") {
            OwnedFocusInterop(this).run()
            return
        }
        if (args.getString("mode") == "media-session") {
            MediaSessionInterop(this).run()
            return
        }
        if (args.getString("mode") == "saf-restore") {
            SharedFolderRestoreInterop(this, args.getString("phase")).run()
            return
        }
        if (args.getString("mode") == "appearance") {
            AppearanceInterop(this, args.getString("evidence")).run()
            return
        }
        if (args.getString("mode") == "route") {
            NativeRouteInterop(this, args).run()
            return
        }
        val result = Bundle()
        var activity: Activity? = null
        var cleanup: (() -> Unit)? = null
        var testMedia: OwnedWavSession? = null
        var stage = "prepare"
        try {
            val pin = requireNotNull(args.getString("pin")).also { require(it.matches(Regex("[A-F0-9]{64}"))) }
            val run = requireNotNull(args.getString("run")).also { require(it.matches(Regex("[a-f0-9]{32}"))) }
            val roaming = args.getString("wifiReturn") == "yes"
            if (roaming) eventFile = File(targetContext.cacheDir, "interop-$run.events").also { check(it.createNewFile()) }
            val key = KeyFactory.getInstance("RSA").generatePublic(X509EncodedKeySpec(
                Base64.decode(requireNotNull(args.getString("wrap")), Base64.URL_SAFE or Base64.NO_WRAP)))
            val cipher = Cipher.getInstance("RSA/ECB/OAEPPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key, OAEPParameterSpec("SHA-256", "MGF1",
                MGF1ParameterSpec.SHA256, PSource.PSpecified.DEFAULT))
            val completed = File(targetContext.cacheDir, "interop-$run.done")
            val lookup = File(targetContext.cacheDir, "interop-$run.media-lookup")
            val keyReceipt = File(targetContext.cacheDir, "interop-$run.media-key")
            var restoreMediaOff = false
            check(!completed.exists())
            val monitor = addMonitor(MainActivity::class.java.name, null, false)
            status("launch", "ready")
            val page = requireNotNull(waitForMonitorWithTimeout(monitor, 25_000)) { "activity_not_launched" }
            removeMonitor(monitor)
            waitForIdleSync()
            activity = page
            val runtime = SonicRouteRuntime.getInstance(targetContext)
            val visibleBy = SystemClock.elapsedRealtime() + 8_000
            while (!ui { page.hasWindowFocus() } && SystemClock.elapsedRealtime() < visibleBy) SystemClock.sleep(80)
            fun press(id: Int) {
                val view = page.findViewById<View>(id)
                check(view.isShown && view.isEnabled)
                view.performClick()
            }
            stage = "initial-state"
            ui {
                val original = runtime.currentSnapshot()
                check(!original.lifecycle.lanEnabled && original.lan.trustedPeers.none { it.fingerprintSha256 == pin })
            }
            stage = "navigate"
            ui {
                press(R.id.nav_pc)
                check(page.findViewById<View>(R.id.panel_pc).isShown)
            }
            cleanup = {
                ui {
                    if (runtime.currentSnapshot().lan.trustedPeers.any { it.fingerprintSha256 == pin }) runtime.revokePeer(pin)
                    runtime.stopLanByUser()
                    if (restoreMediaOff) {
                        runtime.setPublishedMediaEnabled(false)
                        check(!runtime.currentSnapshot().publishedMediaEnabled)
                        result.putString("media_sharing", "restored_off")
                    }
                }
                lookup.delete()
                keyReceipt.delete()
                completed.delete()
            }
            if (args.getString("media") == "yes") {
                stage = "phone-media-consent"
                ui {
                    check(runtime.currentSnapshot().media.accessGranted)
                    if (args.getString("temporaryMediaConsent") == "yes" && !runtime.currentSnapshot().publishedMediaEnabled) {
                        press(R.id.nav_settings)
                        restoreMediaOff = true
                        press(R.id.media_share)
                        press(R.id.nav_pc)
                    }
                    check(runtime.currentSnapshot().publishedMediaEnabled)
                }
                stage = "owned-wav"
                val owned = OwnedWavSession(this)
                testMedia = owned
                owned.prepare(startPlaying = true)
                check(ui { owned.playing })
                status("media", "owned_wav_ready")
            }
            stage = "open-invitation"
            ui { press(R.id.btn_lan_pair) }
            var issued = false
            var approved = false
            var aliasApproved = false
            var fixtureFileApproved = false
            val captureSource = args.getString("captureSource").orEmpty()
            val captureMarker = File(targetContext.cacheDir, "interop-$run.capture")
            var captureRequested = false
            var observed = false
            var connected = false
            var restartStage = 0
            var previousPort = 0
            var stoppedAt = 0L
            var roamStage = 0
            var roamReturnedAt = 0L
            val roamArm = File(targetContext.cacheDir, "interop-$run.roam")
            val restartMarker = File(targetContext.cacheDir, "interop-$run.restart")
            val idleMarker = File(targetContext.cacheDir, "interop-$run.soak-idle")
            val clipboard = if (args.getString("clipboard") == "yes") ClipboardInterop(page, runtime, run, pin) {
                status("clipboard", it)
            } else null
            val start = SystemClock.elapsedRealtime()
            val timeoutMs = (args.getString("seconds")?.toLongOrNull() ?: 360L).coerceIn(60L, 660L) * 1000L
            try { while (SystemClock.elapsedRealtime() - start < timeoutMs) {
                val lan = ui { runtime.currentSnapshot().lan }
                lan.activeInvite?.takeIf { !issued }?.let { invite ->
                    val encoded = invite.toCopyableText().toByteArray(Charsets.UTF_8)
                    val wrapped = try { cipher.doFinal(encoded) } finally { encoded.fill(0) }
                    issued = true
                    status("wrapped", Base64.encodeToString(wrapped, Base64.NO_WRAP))
                }
                if (!approved && lan.pendingPairing != null) {
                    stage = "confirm"
                    ui {
                        val pending = requireNotNull(runtime.currentSnapshot().lan.pendingPairing)
                        check(pending.peerFingerprintSha256 == pin && pending.displayName == "SonicRoute Interop")
                        press(R.id.btn_pair_confirm)
                    }
                    approved = true
                    stage = "observe"
                }
                val now = lan.connectedPeers.any { it.fingerprintSha256 == pin }
                val ownedMedia = testMedia
                if (now && ownedMedia != null && lookup.isFile) {
                    val own = ui { runtime.currentSnapshot().media.players.singleOrNull(ownedMedia::owns) }
                    if (own != null && lookup.delete()) {
                        val tempKey = File(targetContext.cacheDir, "interop-$run.media-key.tmp")
                        try { tempKey.writeText(own.sessionKey); check(tempKey.renameTo(keyReceipt)) }
                        finally { tempKey.delete() }
                    }
                }
                if (roaming) when (roamStage) {
                    0 -> if (now && roamArm.isFile) { roamArm.delete(); roamStage = 1; status("roam", "armed") }
                    1 -> if (!now && lan.waitingForLan && !lan.listening) { roamStage = 2; status("roam", "offline") }
                    2 -> if (now && lan.listening && lan.activeInvite == null && lan.pendingPairing == null) {
                        roamStage = 3; roamReturnedAt = SystemClock.elapsedRealtime(); status("roam", "pinned_peer_returned")
                    }
                    3 -> if (SystemClock.elapsedRealtime() - roamReturnedAt > 12_000) completed.createNewFile()
                }
                if (args.getString("restart") == "yes") when (restartStage) {
                    0 -> if (now && restartMarker.isFile) {
                        restartMarker.delete(); previousPort = lan.port
                        check(lan.trustedPeers.count { it.fingerprintSha256 == pin } == 1)
                        ui { runtime.stopLanByUser() }
                        restartStage = 1
                    }
                    1 -> if (!lan.listening && !now) {
                        stoppedAt = SystemClock.elapsedRealtime(); restartStage = 2; status("restart", "stopped")
                    }
                    2 -> if (SystemClock.elapsedRealtime() - stoppedAt >= 1800) {
                        ui { check(page.hasWindowFocus()); runtime.startLanByUser(false) }
                        restartStage = 3
                    }
                    3 -> if (lan.listening && lan.port in 1..65535) {
                        check(lan.activeInvite == null && lan.pendingPairing == null)
                        restartStage = 4
                        status("restart", if (previousPort == lan.port) "same_port" else "new_port")
                    }
                    4 -> if (now) {
                        check(lan.activeInvite == null && lan.trustedPeers.count { it.fingerprintSha256 == pin } == 1)
                        restartStage = 5; status("restart", "reconnected")
                    }
                }
                if (now && !captureRequested && captureSource in setOf("bin.mt.plus", "cn.wenyu.bodian") &&
                    captureMarker.isFile && ui { page.hasWindowFocus() }) {
                    captureRequested = true
                    captureMarker.delete()
                    ui {
                        check(page.hasWindowFocus() && runtime.currentSnapshot().lan.connectedPeers.count { it.fingerprintSha256 == pin } == 1)
                        press(R.id.capture_playback)
                    }
                    CaptureDialogInterop(this).choosePeerAndSource(pin, captureSource)
                    status("capture", "system_consent_required")
                }
                val alias = lan.aliasBinding
                if (!aliasApproved && args.getString("alias") == "yes" && alias != null && !alias.awaitingProof) {
                    ui {
                        val current = requireNotNull(runtime.currentSnapshot().lan.aliasBinding)
                        check(current.confirmationId == alias.confirmationId && current.peerFingerprintSha256 == pin &&
                            current.displayName == "SonicRoute Interop" && !current.awaitingProof)
                        press(R.id.alias_allow)
                    }
                    aliasApproved = true
                }
                if (!fixtureFileApproved && args.getString("confirmFixtureFile") == "yes") {
                    val offer = ui { runtime.currentSnapshot().files.offer }
                    if (offer != null && offer.name.matches(Regex("SonicRoute-transfer-[a-f0-9]{12}\\.bin")) &&
                        offer.size == 1_048_593L && offer.fingerprint == pin && offer.peer == "SonicRoute Interop") {
                        val marker = File(targetContext.cacheDir, "interop-$run.${offer.name}")
                        if (marker.isFile) {
                            ui {
                                check(runtime.currentSnapshot().files.offer == offer && page.hasWindowFocus())
                                press(R.id.file_offer_accept)
                            }
                            fixtureFileApproved = true
                            marker.delete()
                            status("file", "confirmed_test_receipt")
                        }
                    }
                }
                if (now != connected) {
                    connected = now
                    if (now) observed = true
                    status("peer", if (now) "connected" else "disconnected")
                }
                if (clipboard != null && clipboard.result == null) ui { clipboard.step() }
                if (completed.exists()) break
                if (now && idleMarker.isFile) {
                    idleMarker.delete()
                    val done = CountDownLatch(1)
                    val watcher = object : FileObserver(targetContext.cacheDir, FileObserver.CREATE or FileObserver.MOVED_TO or FileObserver.CLOSE_WRITE) {
                        override fun onEvent(event: Int, path: String?) { if (path == completed.name) done.countDown() }
                    }
                    watcher.startWatching()
                    try {
                        if (!completed.exists()) done.await(timeoutMs - (SystemClock.elapsedRealtime() - start), TimeUnit.MILLISECONDS)
                    } finally { watcher.stopWatching() }
                    if (completed.exists()) break
                }
                if (!approved && SystemClock.elapsedRealtime() - start > 110_000) error("pair timeout")
                SystemClock.sleep(180)
            } } finally {
                if (clipboard != null) {
                    ui { clipboard.close() }
                    result.putString("clipboard", clipboard.result ?: "not_observed")
                }
            }
            check(issued && approved && observed && completed.exists())
            testMedia?.let { owned ->
                if (args.getString("temporaryMediaConsent") == "yes") {
                    check(ui { owned.plays > 0 && owned.pauses > 0 && owned.playing }) { "remote_player_callbacks_missing" }
                    result.putString("remote_media", "pause_and_resume_callbacks_confirmed")
                }
            }
            result.putString("interop", "passed")
        } catch (failure: Exception) {
            val problem = failure.cause ?: failure
            val line = problem.stackTrace.firstOrNull { it.className.contains("LanInteropRunner") }?.lineNumber ?: 0
            result.putString("interop", "failed-$stage-${problem.javaClass.simpleName}-$line")
        } finally {
            runCatching { testMedia?.close() }.onFailure { result.putString("interop", "media-cleanup-failed") }
            runCatching { cleanup?.invoke() }.onFailure { result.putString("interop", "cleanup-failed") }
            activity?.let { page -> ui { page.finish() } }
            eventFile?.let { file ->
                val safe = file.readLines().filter { it.startsWith("INSTRUMENTATION_STATUS: roam=") }
                val resultLine = "INSTRUMENTATION_RESULT: interop=${result.getString("interop")}"
                file.writeText((safe + resultLine).joinToString("\n", postfix = "\n"))
            }
            finish(if (result.getString("interop") == "passed") Activity.RESULT_OK else Activity.RESULT_CANCELED, result)
        }
    }
}
