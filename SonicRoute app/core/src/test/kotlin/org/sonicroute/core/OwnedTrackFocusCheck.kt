package org.sonicroute.core

internal fun checkOwnedTrackFocus() {
    val playback = OwnedPlaybackSnapshot(trackGainPercent = 37, focus = OwnedFocusPhase.HELD)
    check(playback.routedTrackGain(true) == 0.37f && playback.routedTrackGain(false) == 0f)
    val paused = playback.copy(focus = OwnedFocusPhase.PAUSED)
    check(paused.routedTrackGain(true) == 0f && paused.trackGainPercent == 37)
    val userMuted = paused.copy(trackMuted = true, trackGainPercent = 71).copy(focus = OwnedFocusPhase.HELD)
    check(userMuted.routedTrackGain(true) == 0f && userMuted.copy(trackMuted = false).routedTrackGain(true) == 0.71f)
    val callbacks = mutableListOf<(OwnedFocusPhase) -> Unit>()
    var grant = true
    var releases = 0
    val focus = OwnedTrackFocus(false) {
        callbacks += it
        if (grant) AutoCloseable { releases++ } else null
    }
    val local = mutableListOf<OwnedFocusPhase>()
    val pc = mutableListOf<OwnedFocusPhase>()
    check(focus.join("local", local::add) == OwnedFocusPhase.MIXING)
    check(focus.join("pc", pc::add) == OwnedFocusPhase.MIXING && callbacks.isEmpty())
    focus.configure(true)
    check(local == listOf(OwnedFocusPhase.PAUSED, OwnedFocusPhase.HELD) && pc == local)
    val first = callbacks.single()
    first(OwnedFocusPhase.PAUSED)
    check(focus.join("unstarted", {}) == OwnedFocusPhase.DENIED)
    focus.leave("local")
    first(OwnedFocusPhase.HELD)
    check(local.last() == OwnedFocusPhase.PAUSED && pc.last() == OwnedFocusPhase.HELD && releases == 0)
    first(OwnedFocusPhase.LOST)
    check(pc.last() == OwnedFocusPhase.LOST && releases == 1)
    check(focus.join("retry", pc::add) == OwnedFocusPhase.HELD && callbacks.size == 2)
    first(OwnedFocusPhase.LOST)
    check(releases == 1)
    callbacks.last()(OwnedFocusPhase.PAUSED)
    focus.configure(false)
    check(pc.last() == OwnedFocusPhase.MIXING && releases == 2)
    grant = false
    focus.configure(true)
    check(pc.last() == OwnedFocusPhase.DENIED && focus.join("retry", pc::add) == OwnedFocusPhase.DENIED)
    grant = true
    check(focus.join("one", local::add) == OwnedFocusPhase.HELD)
    check(focus.join("two", pc::add) == OwnedFocusPhase.HELD && callbacks.size == 5)
    focus.leave("one"); check(releases == 2)
    focus.leave("two"); check(releases == 3)
}
