package org.sonicroute.core

import java.util.concurrent.Executor

internal fun checkPeerNotices() {
    val scheduled = ArrayDeque<Runnable>()
    val executor = Executor(scheduled::addLast)
    val observed = mutableListOf<String>()
    var retired = 0
    fun peer() = SrapPeerNotices(executor, { observed += "cap${it.capabilityRevision}" },
        { observed += "media" }, { retired++ })
    fun snapshot(revision: Long) = PermissionSnapshot(Backend.STANDARD, 1L, emptyMap(), revision)
    val slow = peer()
    slow.capabilitiesChanged(snapshot(1))
    slow.mediaChanged(); slow.mediaChanged(); slow.capabilitiesChanged(snapshot(2))
    check(scheduled.size == 1 && observed.isEmpty())
    val other = peer()
    other.capabilitiesChanged(snapshot(90))
    scheduled.removeLast().run()
    check(observed == listOf("cap90"))
    scheduled.removeFirst().run()
    check(observed == listOf("cap90", "cap1", "media", "cap2"))
    repeat(20) { slow.capabilitiesChanged(snapshot(it + 3L)) }
    check(retired == 1)
    scheduled.removeFirst().run()
    check(observed.size == 4 && scheduled.isEmpty())
    other.capabilitiesChanged(snapshot(91)); other.close()
    scheduled.removeFirst().run()
    check(observed.size == 4)
}
