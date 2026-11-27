package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.os.Bundle
import android.os.SystemClock
import android.widget.RadioGroup
import org.sonicroute.app.R
import org.sonicroute.core.OwnedFocusPhase
import org.sonicroute.core.OwnedPlaybackState
import org.sonicroute.platform.SonicRouteRuntime

/** Uses Android's focus service and the production muted preview track on an otherwise idle phone. */
internal class OwnedFocusInterop(private val instrument: Instrumentation) {
    fun run() {
        val pages = InteropActivity(instrument)
        val manager = instrument.targetContext.getSystemService(AudioManager::class.java)
        val result = Bundle()
        val rival = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
            .setOnAudioFocusChangeListener { }.build()
        var ownsRival = false
        var restore: (() -> Unit)? = null
        var code = Activity.RESULT_CANCELED
        var stage = "launch"
        try {
            val page = pages.launch()
            val runtime = instrument.onMain {
                page.findViewById<RadioGroup>(R.id.navigation).check(R.id.nav_devices)
                SonicRouteRuntime.getInstance(instrument.targetContext)
            }
            fun state() = instrument.onMain { runtime.currentSnapshot().audio.localPlayback }
            fun awaitState(check: () -> Boolean) {
                val end = SystemClock.elapsedRealtime() + 8000
                do { if (check()) return; SystemClock.sleep(75) } while (SystemClock.elapsedRealtime() < end)
                error("state_readback_timeout")
            }
            stage = "idle-device"
            val before = instrument.onMain { runtime.currentSnapshot().audio }
            check(!manager.isMusicActive && before.localPlayback.state == OwnedPlaybackState.IDLE &&
                before.playback.state == OwnedPlaybackState.IDLE) { "existing_playback" }
            restore = {
                instrument.onMain {
                    val current = runtime.currentSnapshot().audio.localPlayback
                    if (current.state in setOf(OwnedPlaybackState.STARTING, OwnedPlaybackState.PLAYING)) runtime.toggleLocalTestTone()
                    if (runtime.currentSnapshot().audio.localPlayback.trackMuted != before.localPlayback.trackMuted) runtime.toggleOwnedTrackMute()
                    runtime.setOwnedFocusEnabled(before.focusEnabled)
                }
            }
            stage = "initial-focus"
            instrument.onMain {
                runtime.setOwnedFocusEnabled(true)
                if (!before.localPlayback.trackMuted) runtime.toggleOwnedTrackMute()
                check(runtime.currentSnapshot().audio.localPlayback.trackMuted)
                runtime.toggleLocalTestTone()
            }
            awaitState { state().let { it.focus == OwnedFocusPhase.HELD && it.readback?.verified == true && it.state == OwnedPlaybackState.PLAYING } }
            val originalRoute = requireNotNull(state().readback?.actualSink).key
            stage = "transient-focus"
            ownsRival = manager.requestAudioFocus(rival) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
            check(ownsRival) { "second_focus_request_denied" }
            awaitState { state().let { it.focus == OwnedFocusPhase.PAUSED && it.trackMuted } }
            stage = "resume"
            check(manager.abandonAudioFocusRequest(rival) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED)
            ownsRival = false
            awaitState {
                state().let { it.focus == OwnedFocusPhase.HELD && it.state == OwnedPlaybackState.PLAYING &&
                    it.readback?.verified == true && it.readback?.actualSink?.key == originalRoute &&
                    it.trackMuted && it.trackGainPercent == before.localPlayback.trackGainPercent }
            }
            result.putString("owned_focus", "Android focus held, competing transient grant paused the muted AudioTrack, same physical route and gain restored")
            code = Activity.RESULT_OK
        } catch (e: Exception) {
            result.putString("failure", "$stage ${e.cause?.message ?: e.message ?: e.javaClass.simpleName}")
        } finally {
            if (ownsRival) manager.abandonAudioFocusRequest(rival)
            try { restore?.invoke() } finally { pages.close(); instrument.finish(code, result) }
        }
    }
}
