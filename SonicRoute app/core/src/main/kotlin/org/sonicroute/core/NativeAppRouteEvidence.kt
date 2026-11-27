package org.sonicroute.core

/** Actual framework player IDs and the physical output IDs recorded for each active MEDIA track. */
class NativeAppRouteEvidence(val original: Map<Int, Set<Int>>, val target: Int) {
    init {
        require(target > 0 && original.isNotEmpty() && original.keys.all { it > 0 } &&
            original.values.all { it.isNotEmpty() && it.all { id -> id > 0 } })
    }

    fun hasOriginalPlayer(now: Map<Int, Set<Int>>) = original.keys.any { it in now }
    fun isOnTarget(now: Map<Int, Set<Int>>): Boolean = hasOriginalPlayer(now) && now.values.all { it == setOf(target) }
    fun isRestored(now: Map<Int, Set<Int>>): Boolean {
        val remaining = original.keys.intersect(now.keys)
        return remaining.isNotEmpty() && remaining.size == now.size && remaining.all { now[it] == original[it] }
    }
}
