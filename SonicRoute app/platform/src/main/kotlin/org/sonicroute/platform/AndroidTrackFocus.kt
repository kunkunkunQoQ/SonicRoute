package org.sonicroute.platform

import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.os.Handler
import org.sonicroute.core.OwnedFocusPhase
import org.sonicroute.core.OwnedTrackFocus

internal fun ownedMediaAttributes(): AudioAttributes = AudioAttributes.Builder()
    .setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
    .setAllowedCapturePolicy(AudioAttributes.ALLOW_CAPTURE_BY_NONE).build()

internal fun androidTrackFocus(manager: AudioManager, main: Handler, enabled: Boolean) = OwnedTrackFocus(enabled) { change ->
    val request = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
        .setAudioAttributes(ownedMediaAttributes()).setWillPauseWhenDucked(true).setAcceptsDelayedFocusGain(false)
        .setOnAudioFocusChangeListener({ event ->
            when (event) {
                AudioManager.AUDIOFOCUS_GAIN -> change(OwnedFocusPhase.HELD)
                AudioManager.AUDIOFOCUS_LOSS_TRANSIENT, AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK -> change(OwnedFocusPhase.PAUSED)
                AudioManager.AUDIOFOCUS_LOSS -> change(OwnedFocusPhase.LOST)
            }
        }, main).build()
    val granted = runCatching { manager.requestAudioFocus(request) }.getOrNull() == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
    if (granted) AutoCloseable { runCatching { manager.abandonAudioFocusRequest(request) } } else null
}
