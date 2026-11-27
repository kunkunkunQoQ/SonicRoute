package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import android.os.SystemClock
import android.widget.RadioGroup
import org.sonicroute.app.R
import org.sonicroute.core.MediaPlayerSnapshot
import org.sonicroute.core.OperationResult
import org.sonicroute.platform.SonicRouteRuntime
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/** Exercises only a new session backed by this test's muted WAV player. */
internal class MediaSessionInterop(private val instrument: Instrumentation) {
    private fun <T> ui(action: () -> T): T = instrument.onMain(action)

    private fun await(checkNow: () -> Boolean) {
        val end = SystemClock.elapsedRealtime() + 6000
        do { if (checkNow()) return; SystemClock.sleep(80) } while (SystemClock.elapsedRealtime() < end)
        error("media_state_timeout")
    }

    fun run() {
        val pages = InteropActivity(instrument)
        val context = instrument.targetContext
        val result = Bundle()
        var step = "host-launch"
        var code = Activity.RESULT_CANCELED
        val owned = OwnedWavSession(instrument)
        try {
            val page = pages.launch()
            val runtime = ui {
                page.findViewById<RadioGroup>(R.id.navigation).check(R.id.nav_audio)
                SonicRouteRuntime.getInstance(context).also { it.refreshForeground() }
            }
            step = "notification-access"
            check(ui { runtime.currentSnapshot().media.accessGranted })
            step = "wav-player"
            owned.prepare()
            fun own() = runtime.currentSnapshot().media.players.singleOrNull(owned::owns)
            await { ui { own() != null } }
            val key = ui { requireNotNull(own()).sessionKey }
            fun change(action: String, playing: Boolean) {
                val answer = CompletableFuture<OperationResult<MediaPlayerSnapshot>>()
                val cancel = ui {
                    check(own()?.sessionKey == key)
                    runtime.controlLocalMedia(key, action) { answer.complete(it) }
                }
                val state = try { answer.get(5, TimeUnit.SECONDS) } finally { ui { cancel.close() } }
                check(state is OperationResult.Done && state.value.sessionKey == key && owned.owns(state.value) &&
                    state.value.playing == playing && ui { owned.playing == playing })
            }
            step = "verified-play"
            change("play", true)
            val first = ui { owned.position }
            await { ui { owned.position != first && owned.playing } }
            step = "verified-pause"
            change("pause", false)
            val stopped = ui { owned.position }
            SystemClock.sleep(120)
            check(ui { owned.position == stopped && !owned.playing && owned.plays == 1 && owned.pauses == 1 })
            result.putString("media_session", "passed-owned-wav-play-pause")
            code = Activity.RESULT_OK
        } catch (e: Exception) {
            result.putString("media_session", "failed-$step-${e.javaClass.simpleName}")
        } finally {
            owned.close()
            pages.close()
            instrument.finish(code, result)
        }
    }
}
