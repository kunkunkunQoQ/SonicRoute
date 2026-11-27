package org.sonicroute.core

/** A temporary probe is reportable only after synchronous release of its own policy. */
object NativeProbePolicy {
    class ReleaseFailure(reason: String, cause: Exception? = null) : IllegalStateException(reason, cause)

    fun <T> inspect(register: () -> Int, release: (() -> Boolean)?, sample: (Int) -> T): T {
        val unregister = release ?: throw ReleaseFailure("policy_release_unavailable")
        var mayHaveRegistered = true
        try {
            val status = register()
            mayHaveRegistered = status == 0
            return sample(status)
        } finally {
            if (mayHaveRegistered) {
                val released = try { unregister() }
                catch (failure: Exception) { throw ReleaseFailure("policy_release_failed", failure) }
                if (!released) throw ReleaseFailure("policy_release_unconfirmed")
            }
        }
    }
}
