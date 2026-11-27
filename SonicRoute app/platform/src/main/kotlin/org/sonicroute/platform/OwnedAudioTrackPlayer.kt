package org.sonicroute.platform

import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRouting
import android.media.AudioTrack
import android.os.Build
import android.os.Handler
import android.os.Looper
import org.sonicroute.core.AudioOutputSink
import org.sonicroute.core.AudioSinkNormalizer
import org.sonicroute.core.OwnedPlaybackSnapshot
import org.sonicroute.core.OwnedPlaybackState
import org.sonicroute.core.OwnedFocusPhase
import org.sonicroute.core.OwnedTrackFocus
import org.sonicroute.core.ProtocolError
import org.sonicroute.core.RoutedDeviceReadback
import org.sonicroute.core.SrapPcmInput
import kotlin.math.PI
import kotlin.math.sin

/**
 * Self-authored local owned-track AudioTrack engine (48 kHz 16-bit stereo).
 * Reuses MIUI/HyperOS-safe routing patterns: avoids pinning an already-default output on API 33+,
 * stays silent until getRoutedDevice() confirms the target, verifies route changes within 2000 ms,
 * separates owned-track independent gain from system STREAM_MUSIC volume, and releases all resources
 * deterministically on stop/close.
 */
internal class OwnedAudioTrackPlayer(
    private val audioManager: AudioManager,
    private val resolveDeviceInfo: (String) -> AudioDeviceInfo?,
    private val describeDeviceInfo: (AudioDeviceInfo) -> AudioOutputSink,
    private val onStateChanged: (OwnedPlaybackSnapshot) -> Unit,
    private val focus: OwnedTrackFocus,
    initial: OwnedPlaybackSnapshot = OwnedPlaybackSnapshot()
) : AutoCloseable {

    private val main = Handler(Looper.getMainLooper())
    private val lock = java.lang.Object()
    private var snapshot = initial
    private var track: AudioTrack? = null
    private var writerThread: Thread? = null
    private var routingListener: AudioRouting.OnRoutingChangedListener? = null
    private var stopRequested = false
    private var routeVerified = false
    private var closed = false
    private var playbackGeneration = 0L
    private var playbackStarted = false
    private var pauseCount = 0L
    private var pendingRouteObserver: Pair<String, (RoutedDeviceReadback) -> Unit>? = null

    private val routeDeadline = Runnable {
        val currentGen: Long
        val timedOutObserver: ((RoutedDeviceReadback) -> Unit)?
        val target: String
        synchronized(lock) {
            if (closed || stopRequested || track == null || routeVerified || snapshot.focus == OwnedFocusPhase.PAUSED) return@Runnable
            currentGen = playbackGeneration
            target = snapshot.targetKey
            timedOutObserver = pendingRouteObserver?.second
            pendingRouteObserver = null
        }
        timedOutObserver?.invoke(
            RoutedDeviceReadback(
                targetKey = target,
                actualSink = null,
                preferredApplied = false,
                verified = false,
                reason = "route_verification_timeout_2000ms"
            )
        )
        stopInternal(
            error = ProtocolError.TARGET_UNAVAILABLE,
            detail = "route_verification_timeout_2000ms",
            expectedGeneration = currentGen
        )
    }

    fun currentSnapshot(): OwnedPlaybackSnapshot = synchronized(lock) { snapshot }

    fun activeAudioSessionId(): Int = synchronized(lock) {
        track?.takeIf { !stopRequested && it.playState == AudioTrack.PLAYSTATE_PLAYING }?.audioSessionId ?: 0
    }

    fun requestTargetWithReadback(targetKey: String, onRouted: (RoutedDeviceReadback) -> Unit) {
        val alreadyMatched: RoutedDeviceReadback?
        val isPlaying: Boolean
        synchronized(lock) {
            if (closed) {
                onRouted(RoutedDeviceReadback(targetKey, null, false, false, "player_closed"))
                return
            }
            isPlaying = track != null && !stopRequested
            alreadyMatched = if (isPlaying && snapshot.targetKey.equals(targetKey, ignoreCase = true) &&
                snapshot.readback?.verified == true
            ) {
                snapshot.readback
            } else null
        }
        replaceRouteObserver(null)
        if (alreadyMatched != null) {
            onRouted(alreadyMatched)
            return
        }
        if (isPlaying) {
            replaceRouteObserver(targetKey to onRouted)
            synchronized(lock) { snapshot = snapshot.copy(targetKey = targetKey, readback = null, error = null, errorDetail = null) }
            publish()
            applyLiveTargetSwitch(targetKey)
        } else {
            start(targetKey, onRouted)
        }
    }

    private fun replaceRouteObserver(observer: Pair<String, (RoutedDeviceReadback) -> Unit>?) {
        val previous = synchronized(lock) { pendingRouteObserver.also { pendingRouteObserver = observer } }
        previous?.let { (key, callback) ->
            callback(RoutedDeviceReadback(key, null, false, false, "route_request_replaced"))
        }
    }

    fun selectTarget(targetKey: String) {
        var needsSwitch = false
        synchronized(lock) {
            if (closed) return
            if (snapshot.targetKey == targetKey) return
            snapshot = snapshot.copy(targetKey = targetKey, readback = null, error = null, errorDetail = null)
            needsSwitch = track != null && !stopRequested
        }
        publish()
        if (needsSwitch) {
            applyLiveTargetSwitch(targetKey)
        }
    }

    fun setTrackGainPercent(percent: Int) {
        val clamped = percent.coerceIn(0, 100)
        synchronized(lock) {
            if (closed) return
            snapshot = snapshot.copy(trackGainPercent = clamped)
            applyVolumeLocked()
        }
        publish()
    }

    fun setTrackMuted(muted: Boolean) {
        synchronized(lock) {
            if (closed) return
            snapshot = snapshot.copy(trackMuted = muted)
            applyVolumeLocked()
        }
        publish()
    }

    fun onAvailableDevicesChanged(availableSinks: List<AudioOutputSink>) {
        val activePinnedKey: String?
        val currentGen: Long
        synchronized(lock) {
            if (closed || track == null || stopRequested) return
            activePinnedKey = snapshot.targetKey.takeIf { it != AudioOutputSink.KEY_CURRENT }
            currentGen = playbackGeneration
        }
        if (activePinnedKey != null && AudioSinkNormalizer.findSink(availableSinks, activePinnedKey) == null) {
            stopInternal(
                error = ProtocolError.TARGET_UNAVAILABLE,
                detail = "selected_output_disconnected:$activePinnedKey",
                expectedGeneration = currentGen
            )
            return
        }
        verifyRouting()
    }

    fun start(targetKey: String = currentSnapshot().targetKey, onRouted: ((RoutedDeviceReadback) -> Unit)? = null,
        source: SrapPcmInput? = null) {
        stopInternal(error = null, detail = null, expectedGeneration = null)
        val gen: Long
        val newTrack: AudioTrack
        val listener: AudioRouting.OnRoutingChangedListener
        synchronized(lock) {
            if (closed) return
            gen = ++playbackGeneration
            pendingRouteObserver = onRouted?.let { targetKey to it }
            stopRequested = false
            routeVerified = false
            playbackStarted = false
            snapshot = snapshot.copy(
                state = OwnedPlaybackState.STARTING,
                targetKey = targetKey,
                readback = null,
                error = null,
                errorDetail = null
            )
        }
        val initialFocus = focus.join(this) { onFocus(gen, it) }
        synchronized(lock) { snapshot = snapshot.copy(focus = initialFocus) }
        if (initialFocus == OwnedFocusPhase.DENIED) {
            stopInternal(ProtocolError.BUSY, "audio_focus_denied", gen)
            return
        }
        publish()

        try {
            newTrack = createTrack(targetKey)
        } catch (t: Throwable) {
            stopInternal(ProtocolError.TARGET_UNAVAILABLE, t.message ?: t.javaClass.simpleName, gen)
            return
        }

        listener = AudioRouting.OnRoutingChangedListener { router ->
            if (router === newTrack) {
                verifyRouting()
            }
        }
        try { newTrack.addOnRoutingChangedListener(listener, main) } catch (_: RuntimeException) {
            runCatching { newTrack.release() }
            stopInternal(ProtocolError.IO_FAILURE, "audio_routing_listener_failed", gen)
            return
        }

        val thread = Thread({ runAudioLoop(gen, newTrack, source) }, "SonicRoute-owned-track-$gen")
        synchronized(lock) {
            if (closed || playbackGeneration != gen) {
                runCatching { newTrack.removeOnRoutingChangedListener(listener) }
                runCatching { newTrack.release() }
                return
            }
            track = newTrack
            routingListener = listener
            writerThread = thread
        }
        thread.start()
        main.postDelayed(routeDeadline, ROUTE_VERIFY_TIMEOUT_MS)
    }

    fun stop() {
        if (currentSnapshot().state == OwnedPlaybackState.IDLE) return
        stopInternal(error = null, detail = null, expectedGeneration = null)
    }

    private fun onFocus(gen: Long, next: OwnedFocusPhase) {
        var failure: String? = null
        val resume: Boolean
        synchronized(lock) {
            if (closed || stopRequested || playbackGeneration != gen) return
            val previous = snapshot.focus
            if (previous == next) return
            snapshot = snapshot.copy(focus = next)
            resume = previous == OwnedFocusPhase.PAUSED && next in setOf(OwnedFocusPhase.HELD, OwnedFocusPhase.MIXING)
            val active = track
            if (next == OwnedFocusPhase.PAUSED) {
                pauseCount++
                main.removeCallbacks(routeDeadline)
                routeVerified = false
                snapshot = snapshot.copy(readback = null)
                if (active != null && !runCatching { active.setVolume(0f) == AudioTrack.SUCCESS }.getOrDefault(false))
                    failure = "audio_focus_silence_failed"
                if (active != null && playbackStarted) {
                    if (!runCatching { active.pause(); active.playState == AudioTrack.PLAYSTATE_PAUSED }.getOrDefault(false))
                        failure = "audio_focus_pause_failed"
                }
            } else if (resume && active != null && playbackStarted) {
                if (!runCatching { active.play(); active.playState == AudioTrack.PLAYSTATE_PLAYING }.getOrDefault(false))
                    failure = "audio_focus_resume_failed"
            }
            if (failure == null && resume) lock.notifyAll()
        }
        if (next == OwnedFocusPhase.LOST || next == OwnedFocusPhase.DENIED || failure != null) {
            val reason = failure ?: if (next == OwnedFocusPhase.LOST) "audio_focus_lost" else "audio_focus_denied"
            stopInternal(if (failure == null) ProtocolError.BUSY else ProtocolError.IO_FAILURE, reason, gen)
            return
        }
        if (resume) {
            main.removeCallbacks(routeDeadline)
            main.postDelayed(routeDeadline, ROUTE_VERIFY_TIMEOUT_MS)
            verifyRouting()
        }
        publish()
    }

    private fun applyLiveTargetSwitch(targetKey: String) {
        val currentTrack: AudioTrack
        val gen: Long
        synchronized(lock) {
            val active = track ?: return
            currentTrack = active
            gen = playbackGeneration
            routeVerified = false
            active.setVolume(0f)
        }
        val followCurrent = targetKey.equals(AudioOutputSink.KEY_CURRENT, ignoreCase = true)
        val targetDevice = if (followCurrent) null else resolveDeviceInfo(targetKey)
        if (!followCurrent && targetDevice == null) {
            stopInternal(
                error = ProtocolError.TARGET_UNAVAILABLE,
                detail = "target_not_connected:$targetKey",
                expectedGeneration = gen
            )
            return
        }
        val applied = runCatching { currentTrack.setPreferredDevice(targetDevice) }.getOrDefault(false)
        if (!applied) {
            stopInternal(
                error = ProtocolError.TARGET_UNAVAILABLE,
                detail = "setPreferredDevice_rejected:$targetKey",
                expectedGeneration = gen
            )
            return
        }
        main.removeCallbacks(routeDeadline)
        main.postDelayed(routeDeadline, ROUTE_VERIFY_TIMEOUT_MS)
        verifyRouting()
    }

    private fun createTrack(targetKey: String): AudioTrack {
        var followsSystem = targetKey.equals(AudioOutputSink.KEY_CURRENT, ignoreCase = true)
        val expected = if (followsSystem) null else resolveDeviceInfo(targetKey)
        if (!followsSystem && expected == null) {
            throw IllegalStateException("target_not_connected:$targetKey")
        }
        val attributes = ownedMediaAttributes()

        // MIUI/HyperOS-safe check (API 33+): avoid pinning an already-default media sink.
        if (!followsSystem && expected != null && Build.VERSION.SDK_INT >= 33) {
            runCatching {
                val defaults = audioManager.getAudioDevicesForAttributes(attributes)
                if (defaults.any { it.id == expected.id }) {
                    followsSystem = true
                }
            }
        }

        val minBuf = AudioTrack.getMinBufferSize(
            SAMPLE_RATE,
            AudioFormat.CHANNEL_OUT_STEREO,
            AudioFormat.ENCODING_PCM_16BIT
        ).coerceAtLeast(BYTES_PER_CHUNK * 2)
        val bufferBytes = maxOf(minBuf * 2, BYTES_PER_CHUNK * 4)

        val built = AudioTrack.Builder()
            .setPerformanceMode(
                if (followsSystem) AudioTrack.PERFORMANCE_MODE_NONE else AudioTrack.PERFORMANCE_MODE_LOW_LATENCY
            )
            .setAudioAttributes(attributes)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build()
            )
            .setBufferSizeInBytes(bufferBytes)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()

        if (built.state != AudioTrack.STATE_INITIALIZED) {
            built.release()
            throw IllegalStateException("audio_track_uninitialized")
        }

        runCatching {
            if (Build.VERSION.SDK_INT >= 31) {
                built.startThresholdInFrames = minOf(FRAMES_PER_CHUNK, built.bufferSizeInFrames)
            }
        }
        built.setVolume(0f)

        if (!followsSystem) {
            val ok = runCatching { built.setPreferredDevice(expected) }.getOrDefault(false)
            if (!ok) {
                built.release()
                throw IllegalStateException("setPreferredDevice_failed:$targetKey")
            }
        }
        return built
    }

    private fun runAudioLoop(gen: Long, activeTrack: AudioTrack, source: SrapPcmInput?) {
        val pcm = ShortArray(FRAMES_PER_CHUNK * 2)
        var phase = 0.0
        var totalFrames = 0L
        var playingStarted = false
        try {
            if (source != null) {
                playWhenReady(gen, activeTrack)
                // Prime with silence so that routedDevice can be confirmed before moving the PC source.
                val silence = ByteArray(activeTrack.bufferSizeInFrames.coerceIn(FRAMES_PER_CHUNK, 16384) * 4)
                writeFully(gen, activeTrack, silence, 0)
                onPlaybackStarted(gen)
                while (!Thread.currentThread().isInterrupted) {
                    val packet = source.take() ?: return
                    writeFully(gen, activeTrack, packet, SrapPcmInput.HEADER)
                    source.written()
                }
                return
            }
            while (true) {
                synchronized(lock) {
                    if (closed || stopRequested || playbackGeneration != gen) return
                }
                fillTestTone(pcm, phase, totalFrames)
                phase = (phase + FRAMES_PER_CHUNK * PHASE_STEP_A4) % (2.0 * PI)
                totalFrames += FRAMES_PER_CHUNK

                var offset = 0
                while (offset < pcm.size) offset += writeWhenReady(gen) {
                    activeTrack.write(pcm, offset, pcm.size - offset, AudioTrack.WRITE_BLOCKING)
                }
                if (!playingStarted) {
                    playWhenReady(gen, activeTrack)
                    playingStarted = true
                    onPlaybackStarted(gen)
                }
            }
        } catch (_: InterruptedException) {
            // Expected on stop/close.
        } catch (t: Throwable) {
            stopInternal(ProtocolError.IO_FAILURE, t.message ?: t.javaClass.simpleName, gen)
        }
    }

    private fun awaitFocusLocked(gen: Long) {
        while (!closed && !stopRequested && playbackGeneration == gen && snapshot.focus == OwnedFocusPhase.PAUSED) lock.wait()
        if (closed || stopRequested || playbackGeneration != gen || Thread.currentThread().isInterrupted) throw InterruptedException()
    }

    private fun playWhenReady(gen: Long, audio: AudioTrack) {
        synchronized(lock) { awaitFocusLocked(gen); audio.play(); playbackStarted = true }
    }

    private inline fun writeWhenReady(gen: Long, write: () -> Int): Int {
        while (true) {
            val before = synchronized(lock) { awaitFocusLocked(gen); pauseCount }
            val count = write()
            if (count > 0) return count
            if (count == AudioTrack.ERROR_DEAD_OBJECT) throw java.io.IOException("audio_track_dead_object")
            if (synchronized(lock) { before == pauseCount }) throw java.io.IOException("audio_track_write_error:$count")
        }
    }

    private fun writeFully(gen: Long, audio: AudioTrack, bytes: ByteArray, start: Int) {
        var offset = start
        while (offset < bytes.size) {
            offset += writeWhenReady(gen) { audio.write(bytes, offset, bytes.size - offset, AudioTrack.WRITE_BLOCKING) }
        }
    }

    private fun onPlaybackStarted(gen: Long) {
        synchronized(lock) {
            if (playbackGeneration != gen || stopRequested) return
            snapshot = snapshot.copy(state = OwnedPlaybackState.PLAYING)
        }
        main.post {
            publish()
            verifyRouting()
        }
    }

    private fun fillTestTone(buffer: ShortArray, startPhase: Double, startFrame: Long) {
        var p = startPhase
        for (i in 0 until FRAMES_PER_CHUNK) {
            val frameIndex = startFrame + i
            // Gentle 20 ms fade-in to avoid clicks; soft -18 dBFS amplitude (~0.125 * Short.MAX_VALUE).
            val env = if (frameIndex < FADE_FRAMES) frameIndex.toDouble() / FADE_FRAMES else 1.0
            // Subtle amplitude pulse at 1 Hz so listeners immediately recognize the SonicRoute test signal.
            val pulse = 0.75 + 0.25 * sin(2.0 * PI * frameIndex / SAMPLE_RATE)
            val sample = (sin(p) * 0.75 + sin(p * 1.259921) * 0.25) * 4000.0 * env * pulse
            val pcm16 = sample.toInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt()).toShort()
            val idx = i * 2
            buffer[idx] = pcm16
            buffer[idx + 1] = pcm16
            p += PHASE_STEP_A4
        }
    }

    private fun verifyRouting() {
        val currentTrack: AudioTrack
        val targetKey: String
        synchronized(lock) {
            val active = track ?: return
            if (closed || stopRequested || snapshot.focus == OwnedFocusPhase.PAUSED) return
            currentTrack = active
            targetKey = snapshot.targetKey
        }
        val followCurrent = targetKey.equals(AudioOutputSink.KEY_CURRENT, ignoreCase = true)
        val expected = if (followCurrent) null else resolveDeviceInfo(targetKey)
        val actual = runCatching { currentTrack.routedDevice }.getOrNull()
        val matches = actual != null && (followCurrent || (expected != null && actual.id == expected.id))
        val actualSink = actual?.let(describeDeviceInfo)
        val reason = when {
            actual == null -> "awaiting_routed_device"
            matches -> "verified"
            expected == null -> "target_missing"
            else -> "routed_to_other_device(actual=${actualSink?.key ?: actual.id},expected=$targetKey)"
        }
        val readback = RoutedDeviceReadback(
            targetKey = targetKey,
            actualSink = actualSink,
            preferredApplied = followCurrent || currentTrack.preferredDevice != null,
            verified = matches,
            reason = reason
        )

        val observerToNotify: ((RoutedDeviceReadback) -> Unit)?
        val lostVerifiedRoute: Boolean
        synchronized(lock) {
            if (closed || stopRequested || track !== currentTrack) return
            lostVerifiedRoute = routeVerified && !matches
            routeVerified = matches
            applyVolumeLocked()
            snapshot = snapshot.copy(readback = readback)
            observerToNotify = if (matches && pendingRouteObserver?.first?.equals(targetKey, ignoreCase = true) == true) {
                val cb = pendingRouteObserver?.second
                pendingRouteObserver = null
                cb
            } else null
        }
        if (matches) {
            main.removeCallbacks(routeDeadline)
        } else if (lostVerifiedRoute) {
            main.removeCallbacks(routeDeadline)
            main.postDelayed(routeDeadline, ROUTE_VERIFY_TIMEOUT_MS)
        }
        observerToNotify?.invoke(readback)
        main.post { publish() }
    }

    private fun applyVolumeLocked() {
        val active = track ?: return
        runCatching { active.setVolume(snapshot.routedTrackGain(routeVerified)) }
    }

    private fun stopInternal(error: ProtocolError?, detail: String?, expectedGeneration: Long?) {
        if (Looper.myLooper() != main.looper) {
            main.post { stopInternal(error, detail, expectedGeneration) }
            return
        }
        val oldTrack: AudioTrack?
        val oldListener: AudioRouting.OnRoutingChangedListener?
        val oldThread: Thread?
        val droppedObserver: Pair<String, (RoutedDeviceReadback) -> Unit>?
        synchronized(lock) {
            if (expectedGeneration != null && playbackGeneration != expectedGeneration) return
            playbackGeneration++
            stopRequested = true
            playbackStarted = false
            lock.notifyAll()
            routeVerified = false
            oldTrack = track
            oldListener = routingListener
            oldThread = writerThread
            droppedObserver = pendingRouteObserver
            track = null
            routingListener = null
            writerThread = null
            pendingRouteObserver = null
            snapshot = snapshot.copy(
                state = if (error != null) OwnedPlaybackState.ERROR else OwnedPlaybackState.IDLE,
                readback = if (error != null) snapshot.readback else null,
                error = error,
                errorDetail = detail,
                focus = snapshot.focus.takeIf { it == OwnedFocusPhase.DENIED || it == OwnedFocusPhase.LOST } ?: OwnedFocusPhase.INACTIVE
            )
        }
        focus.leave(this)
        droppedObserver?.let { (target, cb) ->
            cb(
                RoutedDeviceReadback(
                    targetKey = target,
                    actualSink = null,
                    preferredApplied = false,
                    verified = false,
                    reason = detail ?: "playback_stopped"
                )
            )
        }
        main.removeCallbacks(routeDeadline)
        oldTrack?.let { t ->
            oldListener?.let { l -> runCatching { t.removeOnRoutingChangedListener(l) } }
            runCatching { t.pause() }
            runCatching { t.flush() }
            runCatching { t.stop() }
        }
        if (oldThread != null && oldThread !== Thread.currentThread()) {
            oldThread.interrupt()
            runCatching { oldThread.join(400) }
        }
        oldTrack?.let { t -> runCatching { t.release() } }
        if (error != null) publish() else main.post { publish() }
    }

    private fun publish() {
        if (Looper.myLooper() != main.looper) {
            main.post { publish() }
            return
        }
        val current = synchronized(lock) {
            if (closed) return
            snapshot
        }
        onStateChanged(current)
    }

    override fun close() {
        synchronized(lock) {
            if (closed) return
            closed = true
        }
        stopInternal(error = null, detail = null, expectedGeneration = null)
    }

    private companion object {
        const val SAMPLE_RATE = 48_000
        const val FRAMES_PER_CHUNK = SAMPLE_RATE / 50 // 20 ms = 960 frames
        const val BYTES_PER_CHUNK = FRAMES_PER_CHUNK * 4
        const val FADE_FRAMES = SAMPLE_RATE / 50
        const val PHASE_STEP_A4 = 2.0 * PI * 440.0 / SAMPLE_RATE
        const val ROUTE_VERIFY_TIMEOUT_MS = 2_000L
    }
}
