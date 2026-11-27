package org.sonicroute.platform

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.Looper
import android.os.Process
import android.os.UserHandle
import org.sonicroute.core.*
import java.util.concurrent.atomic.AtomicBoolean

data class SharedPlaybackState(val app: String, val peer: String, val recording: Boolean)
data class PlaybackCaptureSource(val app: String, val uid: Int, val signer: String, val updatedAt: Long)

/** A local, one-time Android consent for one package and one authenticated computer. */
class SharedPlaybackCapture internal constructor(private val context: Context, private val changed: () -> Unit) {
    private class Grant(val source: PlaybackCaptureSource, val peer: String, val session: String, val token: MediaProjection) {
        val app get() = source.app
        val uid get() = source.uid
        lateinit var callback: MediaProjection.Callback
        var reader: PlaybackCapture? = null
    }
    private val main = Handler(Looper.getMainLooper())
    private var generation = 0L
    @Volatile private var grant: Grant? = null
    private val packageEvents = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.data?.schemeSpecificPart == grant?.app && grant != null) stop()
        }
    }
    private var listeningForPackage = false
    private val expire = Runnable { if (grant?.reader == null) stop() }
    val state get() = grant?.let { SharedPlaybackState(it.app, it.peer, it.reader != null) }
    fun granted(session: String, peer: String) = grant?.let { it.peer == peer && it.session == session } == true
    fun capability(): CapabilityEntry = grant?.let {
        CapabilityEntry(CapabilityStatus.AVAILABLE, scope = "other_app:${it.app}",
            limits = mapOf("pcm" to SrapPcmInput.FORMAT, "mode" to "playback_capture", "grant" to generation.toString()))
    } ?: CapabilityEntry(CapabilityStatus.NEEDS_AUTHORIZATION, reason = "local_playback_capture_consent")

    @Suppress("DEPRECATION")
    fun source(app: String, expectedUid: Int): PlaybackCaptureSource? = runCatching {
        val pm = context.packageManager
        val info = pm.getPackageInfo(app, 0)
        val uid = info.applicationInfo?.uid ?: return null
        if (uid != expectedUid || uid == Process.myUid() || UserHandle.getUserHandleForUid(uid) != Process.myUserHandle() ||
            uid % 100000 < 10000 || pm.getLaunchIntentForPackage(app) == null || pm.getPackagesForUid(uid)?.singleOrNull() != app) return null
        PlaybackCaptureSource(app, uid, NativeAppRoutingIdentity.packageSigner(context, app), info.lastUpdateTime)
    }.getOrNull()

    fun begin(selected: PlaybackCaptureSource, peer: String, session: String, resultCode: Int, data: Intent): Boolean {
        if (source(selected.app, selected.uid) != selected) return false
        if (resultCode != android.app.Activity.RESULT_OK || grant != null ||
            context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED ||
            !SonicRouteConnectionService.promoteProjection(this)) return false
        var projection: MediaProjection? = null
        return try {
            val token = context.getSystemService(MediaProjectionManager::class.java).getMediaProjection(resultCode, data)
                ?: throw IllegalStateException("projection_refused")
            projection = token
            val next = Grant(selected, peer, session, token)
            val packages = IntentFilter().apply {
                addAction(Intent.ACTION_PACKAGE_REMOVED); addAction(Intent.ACTION_PACKAGE_REPLACED)
                addAction(Intent.ACTION_PACKAGE_CHANGED); addDataScheme("package")
            }
            if (android.os.Build.VERSION.SDK_INT >= 33) context.registerReceiver(packageEvents, packages, Context.RECEIVER_NOT_EXPORTED)
            else @Suppress("DEPRECATION") context.registerReceiver(packageEvents, packages)
            listeningForPackage = true
            next.callback = object : MediaProjection.Callback() {
                override fun onStop() { if (grant === next) stop() }
            }
            token.registerCallback(next.callback, main)
            generation++
            grant = next
            main.postDelayed(expire, 90_000)
            changed()
            true
        } catch (_: RuntimeException) {
            grant = null
            detachPackageListener()
            runCatching { projection?.stop() }
            SonicRouteConnectionService.endProjection(this)
            false
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    fun open(session: String, peer: String, app: String): OperationResult<PlaybackCapture> {
        val g = grant?.takeIf { it.app == app && it.peer == peer && it.session == session } ?: return OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED)
        if (g.reader != null) return OperationResult.Failure(ProtocolError.BUSY)
        if (source(app, g.uid) != g.source || context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            stop()
            return OperationResult.Failure(ProtocolError.PERMISSION_LOST)
        }
        var allocated: AudioRecord? = null
        return try {
            val config = AudioPlaybackCaptureConfiguration.Builder(g.token).addMatchingUid(g.uid).build()
            val format = AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(48000)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO).build()
            val min = AudioRecord.getMinBufferSize(48000, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            check(min > 0)
            val audio = AudioRecord.Builder().setAudioFormat(format).setAudioPlaybackCaptureConfig(config)
                .setBufferSizeInBytes(maxOf(min * 2, 7680)).build()
            allocated = audio
            check(audio.state == AudioRecord.STATE_INITIALIZED)
            audio.startRecording()
            check(audio.recordingState == AudioRecord.RECORDSTATE_RECORDING)
            val reader = object : PlaybackCapture {
                private val closed = AtomicBoolean()
                override fun read(buffer: ByteArray, offset: Int, count: Int): Int =
                    if (closed.get()) -1 else audio.read(buffer, offset, count, AudioRecord.READ_BLOCKING)
                override fun close() {
                    if (!closed.compareAndSet(false, true)) return
                    runCatching { audio.stop() }
                    audio.release()
                    main.post { if (grant === g) stop() }
                }
            }
            g.reader = reader
            main.removeCallbacks(expire)
            changed()
            OperationResult.Done(reader)
        } catch (_: RuntimeException) {
            runCatching { allocated?.release() }
            stop()
            OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE, "playback_capture_unavailable")
        }
    }

    fun endSession(session: String) { if (grant?.session == session) stop() }
    fun checkPeers(peers: List<TrustedPeer>) { if (grant?.let { g -> peers.none { it.fingerprintSha256 == g.peer } } == true) stop() }
    fun stop() {
        val g = grant ?: return
        grant = null
        detachPackageListener()
        generation++
        main.removeCallbacks(expire)
        runCatching { g.token.unregisterCallback(g.callback) }
        runCatching { g.reader?.close() }
        runCatching { g.token.stop() }
        SonicRouteConnectionService.endProjection(this)
        changed()
    }

    private fun detachPackageListener() {
        if (!listeningForPackage) return
        listeningForPackage = false
        runCatching { context.unregisterReceiver(packageEvents) }
    }
}
