package org.sonicroute.app.test

import android.app.Instrumentation
import android.media.AudioAttributes
import android.media.MediaMetadata
import android.media.MediaPlayer
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import org.sonicroute.core.MediaPlayerSnapshot
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

/** A test-only muted player. It never requests audio focus or sends a global transport command. */
internal class OwnedWavSession(private val instrument: Instrumentation) : AutoCloseable {
    private val context = instrument.targetContext
    private val marker = "SonicRoute-test-${UUID.randomUUID()}"
    private var file: File? = null
    private var player: MediaPlayer? = null
    private var mediaSession: MediaSession? = null
    private var closed = false
    var plays = 0
        private set
    var pauses = 0
        private set
    val playing get() = player?.isPlaying == true
    val position get() = requireNotNull(player).currentPosition

    fun owns(snapshot: MediaPlayerSnapshot) = snapshot.packageName == context.packageName && snapshot.title == marker

    fun prepare(startPlaying: Boolean = false) {
        val wav = File.createTempFile("srap-media-", ".wav", context.cacheDir)
        file = wav
        writeWav(wav)
        val playback = MediaPlayer()
        player = playback
        playback.setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
        playback.setDataSource(wav.absolutePath)
        playback.setVolume(0f, 0f)
        playback.isLooping = true
        playback.prepare()
        check(playback.duration >= 950)
        instrument.onMain {
            val session = MediaSession(context, marker)
            mediaSession = session
            fun readback() {
                val active = playback.isPlaying
                session.setPlaybackState(PlaybackState.Builder().setActions(PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE)
                    .setState(if (active) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED,
                        playback.currentPosition.toLong(), if (active) 1f else 0f).build())
            }
            session.setMetadata(MediaMetadata.Builder().putString(MediaMetadata.METADATA_KEY_MEDIA_ID, marker)
                .putString(MediaMetadata.METADATA_KEY_TITLE, marker).putLong(MediaMetadata.METADATA_KEY_DURATION, playback.duration.toLong()).build())
            session.setCallback(object : MediaSession.Callback() {
                override fun onPlay() { if (!closed) { plays++; playback.start(); readback() } }
                override fun onPause() { if (!closed) { pauses++; playback.pause(); readback() } }
            }, Handler(Looper.getMainLooper()))
            if (startPlaying) playback.start()
            readback()
            session.isActive = true
        }
    }

    private fun writeWav(target: File) {
        val rate = 16000
        val dataBytes = 2 * rate
        val wav = ByteBuffer.allocate(44 + dataBytes).order(ByteOrder.LITTLE_ENDIAN)
        fun tag(value: String) { wav.put(value.toByteArray(Charsets.US_ASCII)) }
        tag("RIFF"); wav.putInt(36 + dataBytes); tag("WAVE"); tag("fmt ")
        wav.putInt(16).putShort(1).putShort(1).putInt(rate).putInt(rate * 2).putShort(2).putShort(16)
        tag("data"); wav.putInt(dataBytes)
        repeat(rate) { index -> wav.putShort(if (index % 80 < 40) 512 else -512) }
        target.writeBytes(wav.array())
    }

    override fun close() {
        try {
            instrument.onMain {
                closed = true
                try { mediaSession?.let { it.isActive = false; it.setCallback(null); it.release() } }
                finally { player?.release() }
            }
        } finally { file?.delete() }
    }
}
