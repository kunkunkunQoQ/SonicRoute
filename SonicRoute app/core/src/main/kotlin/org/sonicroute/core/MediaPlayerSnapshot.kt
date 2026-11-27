package org.sonicroute.core

const val MAX_SHARED_MEDIA_PLAYERS = 16

/** An active Android MediaSession. Its key expires when observation of this token ends. */
data class MediaPlayerSnapshot(
    val sessionKey: String,
    val packageName: String,
    val appLabel: String,
    val title: String,
    val artist: String,
    val playing: Boolean,
    val state: Int,
    val actions: Long,
    val positionMs: Long,
    val durationMs: Long,
    val speed: Double
)
