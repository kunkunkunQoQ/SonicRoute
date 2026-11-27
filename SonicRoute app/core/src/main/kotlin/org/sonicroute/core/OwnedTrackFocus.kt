package org.sonicroute.core

enum class OwnedFocusPhase { INACTIVE, MIXING, HELD, PAUSED, DENIED, LOST }

/** Main-thread owner of one platform focus request shared by the two app-owned tracks. */
class OwnedTrackFocus(initialEnabled: Boolean, private val request: ((OwnedFocusPhase) -> Unit) -> AutoCloseable?) {
    var enabled = initialEnabled
        private set
    private val users = linkedMapOf<Any, (OwnedFocusPhase) -> Unit>()
    private var lease: AutoCloseable? = null
    private var token: Any? = null
    private var phase = OwnedFocusPhase.INACTIVE

    fun join(owner: Any, changed: (OwnedFocusPhase) -> Unit): OwnedFocusPhase {
        if (users.isEmpty()) phase = if (enabled) acquire() else OwnedFocusPhase.MIXING
        if (phase == OwnedFocusPhase.DENIED || phase == OwnedFocusPhase.PAUSED) return OwnedFocusPhase.DENIED
        users[owner] = changed
        return phase
    }

    fun leave(owner: Any) {
        users.remove(owner)
        if (users.isEmpty()) { release(); phase = OwnedFocusPhase.INACTIVE }
    }

    fun configure(value: Boolean) {
        if (value == enabled) return
        enabled = value
        if (users.isEmpty()) return
        if (!value) { release(); broadcast(OwnedFocusPhase.MIXING); return }
        broadcast(OwnedFocusPhase.PAUSED)
        if (users.isNotEmpty()) broadcast(acquire())
    }

    private fun acquire(): OwnedFocusPhase {
        val id = Any()
        token = id
        lease = request { next ->
            if (token === id) {
                if (next == OwnedFocusPhase.LOST) release()
                broadcast(next)
            }
        }
        if (lease != null) return OwnedFocusPhase.HELD
        token = null
        return OwnedFocusPhase.DENIED
    }

    private fun broadcast(next: OwnedFocusPhase) {
        phase = next
        val recipients = users.toMap()
        if (next == OwnedFocusPhase.DENIED || next == OwnedFocusPhase.LOST) users.clear()
        recipients.forEach { (id, notify) ->
            if (users[id] === notify || next == OwnedFocusPhase.DENIED || next == OwnedFocusPhase.LOST) notify(next)
        }
    }

    private fun release() {
        token = null
        val old = lease
        lease = null
        old?.close()
    }
}
