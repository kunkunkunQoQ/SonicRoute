package org.sonicroute.app

import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.media.session.PlaybackState
import android.os.Build
import android.provider.Settings
import android.view.View
import android.widget.Button
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import org.sonicroute.core.MediaPlayerSnapshot
import org.sonicroute.core.OperationResult
import org.sonicroute.core.ProtocolError
import org.sonicroute.platform.LocalMediaState
import org.sonicroute.platform.MediaAccessService
import org.sonicroute.platform.SonicRouteRuntime

internal class MediaPlayersPage(private val activity: Activity, private val runtime: SonicRouteRuntime) {
    private val status = activity.findViewById<TextView>(R.id.media_status)
    private val access = activity.findViewById<Button>(R.id.media_access)
    private val rows = activity.findViewById<LinearLayout>(R.id.media_sessions)
    private var previous: LocalMediaState? = null
    init { access.setOnClickListener { openSettings() } }

    fun openSettings() {
        if (Build.VERSION.SDK_INT >= 30) {
            val detail = Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS).putExtra(
                Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                ComponentName(activity, MediaAccessService::class.java).flattenToString())
            if (runCatching { activity.startActivity(detail) }.isSuccess) return
        }
        if (runCatching { activity.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) }.isFailure) {
            Toast.makeText(activity, R.string.settings_unavailable, Toast.LENGTH_SHORT).show()
        }
    }

    fun render(current: LocalMediaState) {
        val state = current.copy(players = current.players.map { it.copy(positionMs = 0) })
        if (state == previous) return
        previous = state
        rows.removeAllViews()
        access.visibility = if (!state.accessGranted || state.unavailable) View.VISIBLE else View.GONE
        status.visibility = if (state.players.isEmpty() || state.truncated) View.VISIBLE else View.GONE
        status.setText(when {
            !state.accessGranted -> R.string.media_hint
            state.unavailable -> R.string.media_unavailable
            state.truncated -> R.string.media_truncated
            else -> R.string.media_empty
        })
        for (player in state.players) addPlayer(player)
    }

    private fun addPlayer(player: MediaPlayerSnapshot) {
        val spacing = activity.resources.getDimensionPixelSize(R.dimen.row_spacing)
        val stateText = activity.getString(when (player.state) {
            PlaybackState.STATE_PLAYING -> R.string.media_playing
            PlaybackState.STATE_PAUSED -> R.string.media_paused
            PlaybackState.STATE_STOPPED -> R.string.media_stopped
            PlaybackState.STATE_BUFFERING, PlaybackState.STATE_CONNECTING -> R.string.media_preparing
            PlaybackState.STATE_ERROR -> R.string.media_error
            else -> R.string.media_idle
        })
        rows.addView(TextView(activity).apply {
            setPadding(0, spacing, 0, 0)
            setTextAppearance(R.style.SonicRoute_SectionTitle)
            text = activity.getString(R.string.media_source, player.appLabel, stateText)
        })
        if (player.title.isNotBlank() || player.artist.isNotBlank()) {
            rows.addView(TextView(activity).apply {
                setTextAppearance(R.style.SonicRoute_Body)
                text = listOf(player.title, player.artist).filter { it.isNotBlank() }.joinToString(" · ")
            })
        }
        val controls = ActionStrip(activity)
        fun add(action: String, label: Int, icon: Int, flags: Long) {
            if (player.actions and flags == 0L) return
            controls.addView(ImageButton(activity).apply {
                minimumHeight = resources.getDimensionPixelSize(R.dimen.touch_min_height)
                minimumWidth = minimumHeight
                setBackgroundResource(R.drawable.row_surface)
                imageTintList = activity.getColorStateList(R.color.action_ink)
                setImageResource(icon)
                contentDescription = "${activity.getString(label)} · ${player.appLabel}"
                tooltipText = activity.getString(label)
                setOnClickListener {
                    runtime.controlLocalMedia(player.sessionKey, action) { result ->
                        if (result is OperationResult.Failure && result.error != ProtocolError.CANCELED) {
                            Toast.makeText(activity, R.string.media_not_confirmed, Toast.LENGTH_SHORT).show()
                        }
                    }
                }
            })
        }
        add("previous", R.string.media_previous, R.drawable.ic_previous, PlaybackState.ACTION_SKIP_TO_PREVIOUS)
        if (player.playing) add("pause", R.string.media_pause, R.drawable.ic_pause, PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE)
        else add("play", R.string.media_play, R.drawable.ic_play, PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PLAY_PAUSE)
        add("next", R.string.media_next, R.drawable.ic_track_next, PlaybackState.ACTION_SKIP_TO_NEXT)
        if (controls.childCount > 0) rows.addView(controls)
        else rows.addView(TextView(activity).apply { setText(R.string.media_no_actions) })
    }
}
