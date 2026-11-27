package org.sonicroute.core

import java.security.MessageDigest
import java.util.Locale
import org.sonicroute.core.NativeAppRoutingPipe.hex

data class NativeSavedRoute(val packageName: String, val appLabel: String, val outputLabel: String,
                            val userSerial: Long, val signer: String, val outputKey: String) {
    fun matches(pkg: String, serial: Long, signature: String?) =
        pkg == packageName && serial >= 0 && serial == userSerial && signature == signer

    fun <T> resolve(outputs: List<T>, key: (T) -> String?): T? = outputs.singleOrNull { key(it) == outputKey }

    companion object {
        private val mac = Regex("([A-F0-9]{2}:){5}[A-F0-9]{2}")
        fun bondedOutput(type: Int, address: String, isBonded: (String) -> Boolean): String? {
            val value = address.uppercase(Locale.ROOT)
            if (!mac.matches(value) || value == "02:00:00:00:00:00" || value == "00:00:00:00:00:00" || !isBonded(value)) return null
            return "bonded/$type/" + MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.US_ASCII)).hex()
        }
    }
}
