package org.sonicroute.core

import java.util.Locale

enum class AudioOutputKind(val wireId: String) {
    CURRENT("current"),
    SPEAKER("speaker"),
    BLUETOOTH("bluetooth"),
    WIRED("wired"),
    USB("usb"),
    OTHER("other")
}

data class AudioOutputSink(
    val key: String,
    val kind: AudioOutputKind,
    val androidDeviceId: Int,
    val androidDeviceType: Int,
    val name: String,
    val address: String? = null,
    val isDefault: Boolean = false,
    val isFallbackProfile: Boolean = false
) {
    /**
     * Windows AndroidModels.cs currently parses only speaker / bluetooth / current.
     * Wired and USB sinks are exposed locally and documented for Codex Stage 1/2 alignment.
     */
    val windowsCompatible: Boolean
        get() = kind == AudioOutputKind.CURRENT ||
            kind == AudioOutputKind.SPEAKER ||
            kind == AudioOutputKind.BLUETOOTH

    companion object {
        const val KEY_CURRENT = "current"
        const val KEY_SPEAKER = "speaker"
    }
}

/** Platform-neutral representation of a raw output device before logical normalization. */
data class RawOutputDevice(
    val id: Int,
    val type: Int,
    val kind: AudioOutputKind,
    val productName: String,
    val address: String?,
    val isPrimaryBluetooth: Boolean = false,
    val isFallbackBluetooth: Boolean = false,
    val isDefaultMedia: Boolean = false
)

/**
 * Normalizes Android output devices into stable logical sink keys matching Windows
 * AudioOutputBridge.java / AndroidModels.cs conventions (`current`, `speaker`, `bt:<token>`)
 * plus local `wired:<token>` and `usb:<token>` keys.
 */
object AudioSinkNormalizer {
    private const val ZERO_MAC = "00:00:00:00:00:00"

    fun cleanAddress(raw: String?): String? {
        val trimmed = raw?.trim().orEmpty()
        if (trimmed.isEmpty() || trimmed.equals(ZERO_MAC, ignoreCase = true)) return null
        return if (trimmed.length > 64) trimmed.substring(0, 64) else trimmed
    }

    fun normalizeToken(raw: String): String {
        val sb = StringBuilder(raw.length.coerceAtMost(96))
        for (i in raw.indices) {
            if (sb.length >= 96) break
            val c = raw[i]
            val ok = (c in 'A'..'Z') || (c in 'a'..'z') || (c in '0'..'9') ||
                c == '_' || c == '-' || c == '.' || c == ':' || (c in '\u4E00'..'\u9FFF')
            sb.append(if (ok) c else '_')
        }
        return sb.toString().ifEmpty { "default" }
    }

    fun bluetoothSinkKey(productName: String, address: String?): String {
        val cleanAddr = cleanAddress(address)
        val token = cleanAddr ?: productName.trim().ifEmpty { "bluetooth" }
        return "bt:" + normalizeToken(token)
    }

    fun bluetoothNameAliasKey(productName: String): String =
        "bt:" + normalizeToken(productName.trim().ifEmpty { "bluetooth" })

    fun wiredSinkKey(productName: String, address: String?, type: Int): String {
        val cleanAddr = cleanAddress(address)
        val token = cleanAddr ?: productName.trim().ifEmpty { "type_$type" }
        return "wired:" + normalizeToken(token)
    }

    fun usbSinkKey(productName: String, address: String?, type: Int): String {
        val cleanAddr = cleanAddress(address)
        val token = cleanAddr ?: productName.trim().ifEmpty { "type_$type" }
        return "usb:" + normalizeToken(token)
    }

    fun normalize(
        rawDevices: List<RawOutputDevice>,
        speakerLabel: String = "Phone speaker",
        currentLabel: String = "Follow system output"
    ): List<AudioOutputSink> {
        var speaker: RawOutputDevice? = null
        val primaryBt = LinkedHashMap<String, AudioOutputSink>()
        val seenBtNames = HashSet<String>()
        val wired = LinkedHashMap<String, AudioOutputSink>()
        val usb = LinkedHashMap<String, AudioOutputSink>()

        for (d in rawDevices) {
            when (d.kind) {
                AudioOutputKind.SPEAKER -> if (speaker == null) speaker = d
                AudioOutputKind.BLUETOOTH -> if (d.isPrimaryBluetooth) {
                    val addr = cleanAddress(d.address)
                    val safeName = d.productName.trim().ifEmpty { addr?.let { "Bluetooth $it" } ?: "Bluetooth audio" }
                        .take(96)
                    val key = bluetoothSinkKey(safeName, addr)
                    val nameKey = safeName.lowercase(Locale.ROOT)
                    if (!primaryBt.containsKey(key) && seenBtNames.add(nameKey)) {
                        primaryBt[key] = AudioOutputSink(
                            key = key,
                            kind = AudioOutputKind.BLUETOOTH,
                            androidDeviceId = d.id,
                            androidDeviceType = d.type,
                            name = safeName,
                            address = addr,
                            isDefault = d.isDefaultMedia,
                            isFallbackProfile = false
                        )
                    }
                }
                AudioOutputKind.WIRED -> {
                    val addr = cleanAddress(d.address)
                    val safeName = d.productName.trim().ifEmpty { "Wired audio" }.take(96)
                    val key = wiredSinkKey(safeName, addr, d.type)
                    if (!wired.containsKey(key)) {
                        wired[key] = AudioOutputSink(
                            key = key,
                            kind = AudioOutputKind.WIRED,
                            androidDeviceId = d.id,
                            androidDeviceType = d.type,
                            name = safeName,
                            address = addr,
                            isDefault = d.isDefaultMedia
                        )
                    }
                }
                AudioOutputKind.USB -> {
                    val addr = cleanAddress(d.address)
                    val safeName = d.productName.trim().ifEmpty { "USB audio" }.take(96)
                    val key = usbSinkKey(safeName, addr, d.type)
                    if (!usb.containsKey(key)) {
                        usb[key] = AudioOutputSink(
                            key = key,
                            kind = AudioOutputKind.USB,
                            androidDeviceId = d.id,
                            androidDeviceType = d.type,
                            name = safeName,
                            address = addr,
                            isDefault = d.isDefaultMedia
                        )
                    }
                }
                AudioOutputKind.CURRENT, AudioOutputKind.OTHER -> Unit
            }
        }

        // Fall back to Bluetooth SCO only when no primary A2DP/BLE sink exists.
        if (primaryBt.isEmpty()) {
            for (d in rawDevices) {
                if (d.kind == AudioOutputKind.BLUETOOTH && d.isFallbackBluetooth) {
                    val addr = cleanAddress(d.address)
                    val safeName = d.productName.trim().ifEmpty { addr?.let { "Bluetooth $it" } ?: "Bluetooth SCO" }
                        .take(96)
                    val key = bluetoothSinkKey(safeName, addr)
                    val nameKey = safeName.lowercase(Locale.ROOT)
                    if (!primaryBt.containsKey(key) && seenBtNames.add(nameKey)) {
                        primaryBt[key] = AudioOutputSink(
                            key = key,
                            kind = AudioOutputKind.BLUETOOTH,
                            androidDeviceId = d.id,
                            androidDeviceType = d.type,
                            name = safeName,
                            address = addr,
                            isDefault = d.isDefaultMedia,
                            isFallbackProfile = true
                        )
                    }
                }
            }
        }

        val result = ArrayList<AudioOutputSink>(2 + primaryBt.size + wired.size + usb.size)
        result += AudioOutputSink(
            key = AudioOutputSink.KEY_CURRENT,
            kind = AudioOutputKind.CURRENT,
            androidDeviceId = 0,
            androidDeviceType = 0,
            name = currentLabel,
            isDefault = false
        )
        result += AudioOutputSink(
            key = AudioOutputSink.KEY_SPEAKER,
            kind = AudioOutputKind.SPEAKER,
            androidDeviceId = speaker?.id ?: 0,
            androidDeviceType = speaker?.type ?: 2,
            name = speakerLabel,
            isDefault = speaker?.isDefaultMedia == true
        )
        result.addAll(primaryBt.values)
        result.addAll(wired.values)
        result.addAll(usb.values)
        return result
    }

    fun findSink(sinks: List<AudioOutputSink>, targetKey: String): AudioOutputSink? {
        val exact = sinks.firstOrNull { it.key.equals(targetKey, ignoreCase = true) }
        if (exact != null) return exact
        if (targetKey.startsWith("bt:", ignoreCase = true)) {
            return sinks.firstOrNull {
                it.kind == AudioOutputKind.BLUETOOTH &&
                    bluetoothNameAliasKey(it.name).equals(targetKey, ignoreCase = true)
            }
        }
        return null
    }
}

enum class OwnedPlaybackState { IDLE, STARTING, PLAYING, STOPPING, ERROR }

data class RoutedDeviceReadback(
    val targetKey: String,
    val actualSink: AudioOutputSink?,
    val preferredApplied: Boolean,
    val verified: Boolean,
    val reason: String
)

data class OwnedPlaybackSnapshot(
    val state: OwnedPlaybackState = OwnedPlaybackState.IDLE,
    val targetKey: String = AudioOutputSink.KEY_CURRENT,
    val readback: RoutedDeviceReadback? = null,
    val trackGainPercent: Int = 60,
    val trackMuted: Boolean = false,
    val error: ProtocolError? = null,
    val errorDetail: String? = null,
    val focus: OwnedFocusPhase = OwnedFocusPhase.INACTIVE
) {
    fun routedTrackGain(verified: Boolean): Float =
        if (verified && !trackMuted && focus != OwnedFocusPhase.PAUSED) trackGainPercent.coerceIn(0, 100) / 100f else 0f
}

data class SystemVolumeSnapshot(
    val streamIndex: Int = 0,
    val minIndex: Int = 0,
    val maxIndex: Int = 15,
    val percent: Int = 0,
    val isFixedVolume: Boolean = false,
    val dndRestricted: Boolean = false,
    val isMuted: Boolean? = null
) {
    companion object {
        fun computePercent(index: Int, minIndex: Int, maxIndex: Int): Int {
            if (maxIndex <= minIndex) return 0
            val clamped = index.coerceIn(minIndex, maxIndex)
            return ((clamped - minIndex) * 100) / (maxIndex - minIndex)
        }

        fun indexFromPercent(percent: Int, minIndex: Int, maxIndex: Int): Int {
            if (maxIndex <= minIndex) return minIndex
            val clamped = percent.coerceIn(0, 100)
            return minIndex + ((maxIndex - minIndex) * clamped + 50) / 100
        }
    }
}

data class BluetoothSnapshot(
    val supported: Boolean = false,
    val adapterEnabled: Boolean = false,
    val connectPermissionRequired: Boolean = false,
    val connectPermissionGranted: Boolean = false,
    val bondedAudioNames: List<String> = emptyList(),
    val connectedA2dpNames: List<String> = emptyList(),
    val connectedLeAudioNames: List<String> = emptyList(),
    val detail: String? = null
)

data class AppCatalogEntry(
    val packageName: String,
    val label: String,
    val uid: Int,
    val isSelf: Boolean = false
)

data class AppCatalogSnapshot(
    val visibilityScope: String = "launcher_intents",
    val entries: List<AppCatalogEntry> = emptyList()
)

enum class FeasibilityStatus(val wireId: String) {
    VERIFIED("verified"),
    PARTIAL("partial"),
    BLOCKED_PERMISSION("blocked_permission"),
    BLOCKED_API("blocked_api"),
    UNSUPPORTED("unsupported"),
    NOT_TESTED("not_tested"),
    ERROR("error")
}

data class FeasibilityCheckItem(
    val id: String,
    val capability: Capability,
    val status: FeasibilityStatus,
    val backend: Backend,
    val evidence: String,
    val detail: String
)

data class FeasibilityReport(
    val backend: Backend,
    val backendUid: Int?,
    val generation: Long,
    val apiLevel: Int,
    val deviceModel: String,
    val romSummary: String,
    val items: List<FeasibilityCheckItem>
)
