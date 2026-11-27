package org.sonicroute.platform

import android.app.NotificationManager
import android.content.ComponentName
import android.content.Context
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.service.notification.NotificationListenerService
import android.view.KeyEvent
import org.sonicroute.core.MAX_SHARED_MEDIA_PLAYERS
import org.sonicroute.core.MediaPlayerSnapshot
import org.sonicroute.core.OperationResult
import org.sonicroute.core.ProtocolError
import java.util.UUID
import kotlin.math.abs

/** Android uses this component for the user's notification-listener grant. Only media APIs are used. */
class MediaAccessService : NotificationListenerService() {
    override fun onListenerConnected() { accessChanged?.invoke() }
    override fun onListenerDisconnected() { accessChanged?.invoke() }
    companion object { internal var accessChanged: (() -> Unit)? = null }
}

data class LocalMediaState(
    val accessGranted: Boolean = false,
    val unavailable: Boolean = false,
    val players: List<MediaPlayerSnapshot> = emptyList(),
    val truncated: Boolean = false
)

internal class LocalMediaSessions(private val context: Context, private val changed: () -> Unit) {
    private val manager = context.getSystemService(MediaSessionManager::class.java)
    private val notifications = context.getSystemService(NotificationManager::class.java)
    private val component = ComponentName(context, MediaAccessService::class.java)
    private val main = Handler(Looper.getMainLooper())
    private val rows = linkedMapOf<MediaSession.Token, Row>()
    private var priority = emptyList<MediaSession.Token>()
    private var needed = false
    private var registered = false
    var state = LocalMediaState()
        private set
    private val listener = MediaSessionManager.OnActiveSessionsChangedListener { controllers ->
        if (registered) {
            if (hasAccess()) updateControllers(controllers.orEmpty()) else refresh()
        }
    }
    private val noOperation = AutoCloseable {}

    init { MediaAccessService.accessChanged = ::refresh }

    private inner class Row(val controller: MediaController) {
        val key = UUID.randomUUID().toString()
        val label = runCatching {
            context.packageManager.getApplicationLabel(
                context.packageManager.getApplicationInfo(controller.packageName, 0)
            ).toString()
        }.getOrDefault(controller.packageName).take(128)
        var pending: Pending? = null
        val callback = object : MediaController.Callback() {
            override fun onPlaybackStateChanged(state: PlaybackState?) = updated()
            override fun onMetadataChanged(metadata: MediaMetadata?) = updated()
            override fun onSessionDestroyed() {
                if (rows[controller.sessionToken] === this@Row) {
                    rows.remove(controller.sessionToken)
                    dispose(ProtocolError.TARGET_UNAVAILABLE)
                    emit()
                }
            }
        }

        fun updated() {
            if (!registered || rows[controller.sessionToken] !== this) return
            if (!hasAccess()) { refresh(); return }
            runCatching { verify(this) }
            emit()
        }

        fun read(): MediaPlayerSnapshot {
            val playback = controller.playbackState
            val metadata = controller.metadata
            val playing = playback?.state == PlaybackState.STATE_PLAYING
            val duration = metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION)?.coerceIn(0, MAX_DURATION) ?: 0
            val speed = playback?.playbackSpeed?.toDouble()?.takeIf { it.isFinite() } ?: 1.0
            val updatedAt = playback?.lastPositionUpdateTime ?: 0L
            val age = if (playing && updatedAt > 0)
                (SystemClock.elapsedRealtime() - updatedAt).coerceAtLeast(0) else 0
            val rawPosition = playback?.position ?: -1L
            val position = if (rawPosition < 0) 0 else (rawPosition + age * speed).toLong()
            return MediaPlayerSnapshot(
                key, controller.packageName.take(256), label,
                (metadata?.getText(MediaMetadata.METADATA_KEY_TITLE)
                    ?: metadata?.getText(MediaMetadata.METADATA_KEY_DISPLAY_TITLE))?.toString().orEmpty().take(256),
                (metadata?.getText(MediaMetadata.METADATA_KEY_ARTIST)
                    ?: metadata?.getText(MediaMetadata.METADATA_KEY_ALBUM_ARTIST))?.toString().orEmpty().take(256),
                playing, playback?.state ?: PlaybackState.STATE_NONE, playback?.actions ?: 0L,
                position.coerceIn(0, if (duration > 0) duration else MAX_DURATION), duration, speed
            )
        }

        fun trackIdentity(): List<String> {
            val metadata = controller.metadata
            fun text(key: String) = metadata?.getText(key)?.toString().orEmpty().take(512)
            return listOf(text(MediaMetadata.METADATA_KEY_MEDIA_ID), text(MediaMetadata.METADATA_KEY_TITLE),
                text(MediaMetadata.METADATA_KEY_DISPLAY_TITLE), text(MediaMetadata.METADATA_KEY_ARTIST),
                controller.playbackState?.activeQueueItemId?.takeUnless { it == MediaSession.QueueItem.UNKNOWN_ID.toLong() }
                    ?.toString().orEmpty())
        }

        fun dispose(error: ProtocolError) {
            runCatching { controller.unregisterCallback(callback) }
            pending?.let { finish(this, it, OperationResult.Failure(error)) }
        }
    }

    private class Pending(val action: String, val position: Long?, val original: List<String>,
                          val oldPlayback: PlaybackState?, val shared: Boolean,
                          val callback: (OperationResult<MediaPlayerSnapshot>) -> Unit) {
        lateinit var deadline: Runnable
    }

    fun hasAccess() = runCatching { notifications.isNotificationListenerAccessGranted(component) }.getOrDefault(false)

    fun setNeeded(value: Boolean) {
        if (needed == value) return
        needed = value
        refresh()
    }

    fun refresh() {
        val granted = hasAccess()
        if (!needed || !granted) {
            stop(if (granted) ProtocolError.CANCELED else ProtocolError.PERMISSION_LOST)
            setState(LocalMediaState(granted))
            return
        }
        try {
            if (!registered) {
                manager.addOnActiveSessionsChangedListener(listener, component, main)
                registered = true
            }
            updateControllers(manager.getActiveSessions(component))
        } catch (_: SecurityException) {
            stop(ProtocolError.PERMISSION_LOST)
            setState(LocalMediaState(hasAccess(), unavailable = true))
        } catch (_: RuntimeException) {
            stop(ProtocolError.IO_FAILURE)
            setState(LocalMediaState(granted, unavailable = true))
        }
    }

    private fun updateControllers(controllers: List<MediaController>) {
        val active = controllers.asSequence().distinctBy { it.sessionToken }.take(MAX_SHARED_MEDIA_PLAYERS + 1).toList()
        priority = active.map { it.sessionToken }
        for (token in rows.keys - priority.toSet()) rows.remove(token)?.dispose(ProtocolError.TARGET_UNAVAILABLE)
        for (controller in active) {
            if (controller.sessionToken in rows) continue
            val row = Row(controller)
            if (runCatching { controller.registerCallback(row.callback, main) }.isSuccess) {
                rows[controller.sessionToken] = row
            }
        }
        emit()
    }

    private fun emit() {
        val available = snapshots(MAX_SHARED_MEDIA_PLAYERS + 1)
        setState(LocalMediaState(accessGranted = true, players = available.take(MAX_SHARED_MEDIA_PLAYERS),
            truncated = available.size > MAX_SHARED_MEDIA_PLAYERS))
    }
    private fun snapshots(limit: Int) = priority.asSequence().mapNotNull(rows::get)
        .mapNotNull { runCatching { it.read() }.getOrNull() }.take(limit).toList()

    private fun setState(value: LocalMediaState) {
        if (value != state) { state = value; changed() }
    }

    private fun stop(error: ProtocolError) {
        if (registered) runCatching { manager.removeOnActiveSessionsChangedListener(listener) }
        registered = false
        val previous = rows.values.toList()
        rows.clear()
        priority = emptyList()
        previous.forEach { it.dispose(error) }
    }

    fun read(): OperationResult<List<MediaPlayerSnapshot>> {
        if (!hasAccess()) { refresh(); return OperationResult.Failure(ProtocolError.PERMISSION_LOST) }
        if (!registered) return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE)
        return OperationResult.Done(snapshots(MAX_SHARED_MEDIA_PLAYERS + 1))
    }

    fun readPackageState(packageName: String): OperationResult<Boolean> {
        if (!hasAccess()) { refresh(); return OperationResult.Failure(ProtocolError.PERMISSION_LOST) }
        if (!registered) return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE)
        return try {
            val matches = manager.getActiveSessions(component).asSequence().filter { it.packageName == packageName }
                .distinctBy { it.sessionToken }.take(2).toList()
            if (matches.size > 1) return OperationResult.Failure(ProtocolError.BUSY, "multiple_media_sessions")
            val state = matches.singleOrNull()?.playbackState?.state ?: return OperationResult.Failure(ProtocolError.TARGET_UNAVAILABLE)
            if (!hasAccess()) OperationResult.Failure(ProtocolError.PERMISSION_LOST)
            else OperationResult.Done(state == PlaybackState.STATE_PLAYING)
        } catch (_: SecurityException) {
            refresh()
            OperationResult.Failure(ProtocolError.PERMISSION_LOST)
        } catch (_: RuntimeException) { OperationResult.Failure(ProtocolError.IO_FAILURE) }
    }

    fun control(key: String, action: String, position: Long?, shared: Boolean,
                callback: (OperationResult<MediaPlayerSnapshot>) -> Unit): AutoCloseable {
        fun fail(error: ProtocolError): AutoCloseable {
            callback(OperationResult.Failure(error))
            return noOperation
        }
        if (!hasAccess()) { refresh(); return fail(ProtocolError.PERMISSION_LOST) }
        val row = rows.values.firstOrNull { it.key == key } ?: return fail(ProtocolError.TARGET_UNAVAILABLE)
        if (row.pending != null) return fail(ProtocolError.BUSY)
        val active = try { manager.getActiveSessions(component) } catch (_: SecurityException) {
            refresh()
            return fail(ProtocolError.PERMISSION_LOST)
        } catch (_: RuntimeException) { return fail(ProtocolError.IO_FAILURE) }
        if (active.none { it.sessionToken == row.controller.sessionToken }) {
            updateControllers(active)
            return fail(ProtocolError.TARGET_UNAVAILABLE)
        }
        val before = runCatching { row.read() }.getOrNull() ?: return fail(ProtocolError.TARGET_UNAVAILABLE)
        val required = when (action) {
            "play" -> PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PLAY_PAUSE
            "pause" -> PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE
            "previous" -> PlaybackState.ACTION_SKIP_TO_PREVIOUS
            "next" -> PlaybackState.ACTION_SKIP_TO_NEXT
            "seek" -> PlaybackState.ACTION_SEEK_TO
            else -> return fail(ProtocolError.INVALID_ARGUMENT)
        }
        if (before.actions and required == 0L) return fail(ProtocolError.UNSUPPORTED)
        if (action == "seek" && (before.durationMs <= 0 || position == null || position !in 0..before.durationMs) ||
            action != "seek" && position != null) return fail(ProtocolError.INVALID_ARGUMENT)
        if (action == "play" && before.playing || action == "pause" && before.state == PlaybackState.STATE_PAUSED ||
            action == "seek" && before.positionMs == position && row.controller.playbackState?.position?.let { it >= 0 } == true) {
            callback(OperationResult.Done(before))
            return noOperation
        }
        val original = runCatching { row.trackIdentity() }.getOrNull() ?: return fail(ProtocolError.TARGET_UNAVAILABLE)
        val request = Pending(action, position, original, row.controller.playbackState, shared, callback)
        request.deadline = Runnable {
            if (row.pending !== request) return@Runnable
            if (!hasAccess()) { refresh(); return@Runnable }
            runCatching { verify(row) }
            if (row.pending === request) finish(row, request, OperationResult.Failure(ProtocolError.TIMEOUT))
        }
        row.pending = request
        main.postDelayed(request.deadline, 2500)
        try {
            val transport = row.controller.transportControls
            when (action) {
                "play", "pause" -> {
                    val direct = if (action == "play") PlaybackState.ACTION_PLAY else PlaybackState.ACTION_PAUSE
                    if (before.actions and direct != 0L) {
                        if (action == "play") transport.play() else transport.pause()
                    } else {
                        val accepted = row.controller.dispatchMediaButtonEvent(KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE))
                        row.controller.dispatchMediaButtonEvent(KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE))
                        if (!accepted) finish(row, request, OperationResult.Failure(ProtocolError.UNSUPPORTED))
                    }
                }
                "next" -> transport.skipToNext()
                "previous" -> transport.skipToPrevious()
                "seek" -> transport.seekTo(requireNotNull(position))
            }
        } catch (_: SecurityException) {
            finish(row, request, OperationResult.Failure(ProtocolError.PERMISSION_LOST))
        } catch (_: RuntimeException) {
            finish(row, request, OperationResult.Failure(ProtocolError.IO_FAILURE))
        }
        return AutoCloseable {
            val cancel = Runnable { if (row.pending === request) finish(row, request, OperationResult.Failure(ProtocolError.CANCELED)) }
            if (Looper.myLooper() == main.looper) cancel.run() else main.post(cancel)
        }
    }

    private fun verify(row: Row) {
        val pending = row.pending ?: return
        val actual = row.read()
        val matches = when (pending.action) {
            "play" -> actual.playing
            "pause" -> actual.state == PlaybackState.STATE_PAUSED
            "seek" -> row.controller.playbackState?.let { observed ->
                val newAnchor = observed.lastPositionUpdateTime != pending.oldPlayback?.lastPositionUpdateTime ||
                    observed.position != pending.oldPlayback?.position
                newAnchor && observed.position >= 0 && abs(observed.position - requireNotNull(pending.position)) <= 1000
            } == true
            else -> row.trackIdentity().let { now ->
                now.indices.any { i -> now[i].isNotEmpty() && pending.original[i].isNotEmpty() && now[i] != pending.original[i] }
            }
        }
        if (matches) finish(row, pending, OperationResult.Done(actual))
    }

    fun cancelCommands(shared: Boolean) {
        rows.values.toList().forEach { row ->
            row.pending?.takeIf { it.shared == shared }?.let { finish(row, it, OperationResult.Failure(ProtocolError.CANCELED)) }
        }
    }

    private fun finish(row: Row, request: Pending, result: OperationResult<MediaPlayerSnapshot>) {
        if (row.pending !== request) return
        row.pending = null
        main.removeCallbacks(request.deadline)
        request.callback(result)
    }

    private companion object { const val MAX_DURATION = 604_800_000L }
}
