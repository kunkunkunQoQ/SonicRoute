package org.sonicroute.platform

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import org.sonicroute.core.AppCatalogSnapshot
import org.sonicroute.core.LocalAppCatalog
import org.sonicroute.core.AudioOutputKind
import org.sonicroute.core.AudioOutputSink
import org.sonicroute.core.AudioSinkNormalizer
import org.sonicroute.core.BluetoothSnapshot
import org.sonicroute.core.Capability
import org.sonicroute.core.CapabilityEntry
import org.sonicroute.core.CapabilityStatus
import org.sonicroute.core.OperationResult
import org.sonicroute.core.OwnedPlaybackSnapshot
import org.sonicroute.core.ProtocolError
import org.sonicroute.core.RawOutputDevice
import org.sonicroute.core.RuleResult
import org.sonicroute.core.SystemVolumeSnapshot
import java.util.concurrent.Executors

enum class LauncherCatalogState { IDLE, LOADING, READY, FAILED }

data class LocalAudioState(
    val outputs: List<AudioOutputSink> = emptyList(),
    val playback: OwnedPlaybackSnapshot = OwnedPlaybackSnapshot(),
    val localPlayback: OwnedPlaybackSnapshot = OwnedPlaybackSnapshot(),
    val volume: SystemVolumeSnapshot = SystemVolumeSnapshot(),
    val bluetooth: BluetoothSnapshot = BluetoothSnapshot(),
    val appCatalog: AppCatalogSnapshot = AppCatalogSnapshot(),
    val catalogState: LauncherCatalogState = LauncherCatalogState.IDLE,
    val focusEnabled: Boolean = false
)

/**
 * Single foreground owner for Roadmap Stage 3 local audio devices, Bluetooth inspection/settings,
 * system media volume, owned-track playback + routing readback, and constrained app catalog.
 * Uses Android system callbacks exclusively (no idle polling) and releases every callback, proxy,
 * receiver, and AudioTrack on close().
 */
class LocalAudioController(
    context: Context,
    private var speakerLabel: String,
    private var currentLabel: String,
    private val onStateChanged: (LocalAudioState) -> Unit,
    private val onLocalCapabilitiesObserved: (Map<Capability, CapabilityEntry>) -> Unit,
    private val onOutputKindsObserved: (Set<AudioOutputKind>) -> Unit
) : AutoCloseable {

    private val appContext = context.applicationContext
    private val audioManager = appContext.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val bluetoothManager = appContext.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor { r ->
        Thread(r, "SonicRoute-local-audio").apply { isDaemon = true }
    }

    private val localPreferences = appContext.getSharedPreferences("local_playback", Context.MODE_PRIVATE)
    private val trackFocus = androidTrackFocus(audioManager, main, localPreferences.getBoolean("audio_focus", false))
    @Volatile private var state = LocalAudioState(localPlayback = OwnedPlaybackSnapshot(
        targetKey = localPreferences.getString("output", AudioOutputSink.KEY_CURRENT) ?: AudioOutputSink.KEY_CURRENT,
        trackGainPercent = localPreferences.getInt("gain", OwnedPlaybackSnapshot().trackGainPercent).coerceIn(0, 100),
        trackMuted = localPreferences.getBoolean("muted", false)
    ), focusEnabled = trackFocus.enabled)
    private var localPreferencesDirty = false
    private var deviceByKey: Map<String, AudioDeviceInfo> = emptyMap()
    private var a2dpProxy: BluetoothProfile? = null
    private var leAudioProxy: BluetoothProfile? = null
    private var volumeReceiverRegistered = false
    private var deviceCallbackRegistered = false
    @Volatile private var closed = false
    private var automationMonitoring = false
    @Volatile private var catalogRevision = 0L
    private var catalogLoading = false
    private val catalogIndex = LocalAppCatalog()
    private val changedPackages = mutableSetOf<String>()
    private var catalogNeedsScan = false
    private var catalogNeeded = false
    private var packageReceiverRegistered = false

    private val player = OwnedAudioTrackPlayer(
        audioManager = audioManager,
        focus = trackFocus,
        resolveDeviceInfo = ::resolveAudioDeviceInfo,
        describeDeviceInfo = ::describeAudioDevice,
        onStateChanged = { playback ->
            if (!closed) {
                state = state.copy(playback = playback)
                publish()
            }
        }
    )

    private val localPlayer = OwnedAudioTrackPlayer(
        audioManager, ::resolveAudioDeviceInfo, ::describeAudioDevice,
        focus = trackFocus,
        onStateChanged = {
            if (!closed) {
                state = state.copy(localPlayback = it)
                publish()
            }
        },
        initial = state.localPlayback
    )

    private val audioDeviceCallback = object : AudioDeviceCallback() {
        override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>?) {
            onDeviceEvent()
        }

        override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>?) {
            onDeviceEvent()
        }
    }

    private val volumeReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (!observersActive) return
            when (intent?.action) {
                Intent.ACTION_LOCALE_CHANGED -> invalidateCatalog(emptySet())
                Intent.ACTION_EXTERNAL_APPLICATIONS_AVAILABLE, Intent.ACTION_EXTERNAL_APPLICATIONS_UNAVAILABLE ->
                    invalidateCatalog(intent.getStringArrayExtra(Intent.EXTRA_CHANGED_PACKAGE_LIST)?.toSet().orEmpty())
                else -> refreshSystemVolume()
            }
        }
    }

    private val packageReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (observersActive) intent?.data?.schemeSpecificPart?.let { invalidateCatalog(setOf(it)) }
        }
    }

    private val profileListeners = mutableMapOf<Int, BluetoothProfile.ServiceListener>()

    private var observersActive = false

    fun start() = setObserversActive(true)

    /**
     * Power-gating hook: registers AudioDeviceCallback, VOLUME_CHANGED receiver, and Bluetooth
     * proxies ONLY when UI is visible or at least one trusted PC session is connected.
     */
    fun setObserversActive(active: Boolean) {
        if (closed || observersActive == active) return
        observersActive = active
        updateDeviceCallback()
        if (active) {
            if (!volumeReceiverRegistered) {
                val filter = IntentFilter(ACTION_VOLUME_CHANGED).apply {
                    addAction(Intent.ACTION_LOCALE_CHANGED)
                    addAction(Intent.ACTION_EXTERNAL_APPLICATIONS_AVAILABLE)
                    addAction(Intent.ACTION_EXTERNAL_APPLICATIONS_UNAVAILABLE)
                }
                if (Build.VERSION.SDK_INT >= 33) {
                    appContext.registerReceiver(volumeReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
                } else {
                    @Suppress("DEPRECATION")
                    appContext.registerReceiver(volumeReceiver, filter)
                }
                volumeReceiverRegistered = true
            }
            if (!packageReceiverRegistered) {
                val filter = IntentFilter().apply {
                    addAction(Intent.ACTION_PACKAGE_ADDED)
                    addAction(Intent.ACTION_PACKAGE_REMOVED)
                    addAction(Intent.ACTION_PACKAGE_CHANGED)
                    addAction(Intent.ACTION_PACKAGE_REPLACED)
                    addDataScheme("package")
                }
                if (Build.VERSION.SDK_INT >= 33) appContext.registerReceiver(packageReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
                else appContext.registerReceiver(packageReceiver, filter)
                packageReceiverRegistered = true
            }
            refreshAll()
        } else {
            detachSystemObservers()
        }
    }

    fun setAutomationMonitoring(active: Boolean) {
        if (closed || automationMonitoring == active) return
        automationMonitoring = active
        updateDeviceCallback()
        if (active) refreshAudioDevices()
    }

    fun setCatalogNeeded(needed: Boolean) {
        if (closed || needed == catalogNeeded) return
        catalogNeeded = needed
        catalogRevision++
        if (needed) refreshAppCatalogAsync() else setPublishedCatalog(LauncherCatalogState.IDLE)
    }

    fun retryCatalogIfFailed() {
        if (state.catalogState == LauncherCatalogState.FAILED) refreshAppCatalogAsync()
    }

    private fun updateDeviceCallback() {
        val needed = !closed && (observersActive || automationMonitoring)
        if (needed == deviceCallbackRegistered) return
        if (needed) {
            audioManager.registerAudioDeviceCallback(audioDeviceCallback, main)
            deviceCallbackRegistered = true
        } else {
            runCatching { audioManager.unregisterAudioDeviceCallback(audioDeviceCallback) }
            deviceCallbackRegistered = false
        }
    }

    private fun detachSystemObservers() {
        if (packageReceiverRegistered) {
            runCatching { appContext.unregisterReceiver(packageReceiver) }
            packageReceiverRegistered = false
        }
        if (volumeReceiverRegistered) {
            runCatching { appContext.unregisterReceiver(volumeReceiver) }
            volumeReceiverRegistered = false
        }
        detachBluetoothProfiles()
    }

    private fun onDeviceEvent() {
        if (!deviceCallbackRegistered) return
        refreshAudioDevices()
        if (observersActive) refreshBluetooth()
    }

    private fun detachBluetoothProfiles() {
        profileListeners.clear()
        val adapter = bluetoothManager?.adapter
        a2dpProxy?.let { p -> runCatching { adapter?.closeProfileProxy(BluetoothProfile.A2DP, p) } }
        a2dpProxy = null
        if (Build.VERSION.SDK_INT >= 33) {
            leAudioProxy?.let { p -> runCatching { adapter?.closeProfileProxy(BluetoothProfile.LE_AUDIO, p) } }
        }
        leAudioProxy = null
    }

    fun refreshAll() {
        if (closed || !observersActive) return
        refreshAudioDevices()
        refreshSystemVolume()
        attachBluetoothProxiesIfPermitted()
        refreshBluetooth()
    }

    fun updateOutputLabels(speaker: String, current: String) {
        if (speakerLabel == speaker && currentLabel == current) return
        speakerLabel = speaker
        currentLabel = current
        state = state.copy(outputs = state.outputs.map {
            when (it.kind) {
                AudioOutputKind.CURRENT -> it.copy(name = current)
                AudioOutputKind.SPEAKER -> it.copy(name = speaker)
                else -> it
            }
        })
        publish()
    }

    fun currentState(): LocalAudioState = state

    fun activeSelfAudioSessionId(): Int = localPlayer.activeAudioSessionId()

    fun selectLocalOutput(key: String) {
        if (closed) return
        localPlayer.selectTarget(key)
        localPreferencesDirty = true
        saveLocalPreferences()
    }

    fun startLocalTestTone() = localPlayer.start()
    fun stopLocalTestTone() = localPlayer.stop()
    fun setOwnedFocusEnabled(enabled: Boolean) {
        if (closed || trackFocus.enabled == enabled) return
        state = state.copy(focusEnabled = enabled)
        localPreferences.edit().putBoolean("audio_focus", enabled).apply()
        trackFocus.configure(enabled)
        publish()
    }
    fun setLocalTrackGain(percent: Int) {
        localPlayer.setTrackGainPercent(percent)
        localPreferencesDirty = true
    }

    fun setLocalTrackMuted(muted: Boolean) {
        localPlayer.setTrackMuted(muted)
        localPreferencesDirty = true
        saveLocalPreferences()
    }

    fun saveLocalPreferences() {
        if (!localPreferencesDirty) return
        val playback = localPlayer.currentSnapshot()
        localPreferences.edit().putString("output", playback.targetKey)
            .putInt("gain", playback.trackGainPercent).putBoolean("muted", playback.trackMuted).apply()
        localPreferencesDirty = false
    }

    fun selectOutputTarget(targetKey: String) {
        if (!closed) player.selectTarget(targetKey)
    }

    fun requestOwnedRouteWithReadback(targetKey: String, onRouted: (org.sonicroute.core.RoutedDeviceReadback) -> Unit) {
        if (closed) {
            onRouted(org.sonicroute.core.RoutedDeviceReadback(targetKey, null, false, false, "controller_closed"))
            return
        }
        player.requestTargetWithReadback(targetKey, onRouted)
    }

    fun startTestTone(targetKey: String = state.playback.targetKey) {
        if (!closed) player.start(targetKey)
    }

    fun startNetworkPcm(targetKey: String, source: org.sonicroute.core.SrapPcmInput) {
        if (!closed) player.start(targetKey, source = source)
    }

    fun stopTestTone() {
        if (!closed) player.stop()
    }

    fun setOwnedTrackGainPercent(percent: Int) {
        if (!closed) player.setTrackGainPercent(percent)
    }

    fun setOwnedTrackMuted(muted: Boolean) {
        if (!closed) player.setTrackMuted(muted)
    }

    fun setSystemMediaVolumePercent(percent: Int) = setSystemMediaVolume { min, max ->
        SystemVolumeSnapshot.indexFromPercent(percent, min, max)
    }

    fun setSystemMediaVolumeIndex(index: Int) = setSystemMediaVolume { _, _ -> index }

    private inline fun setSystemMediaVolume(target: (Int, Int) -> Int): OperationResult<SystemVolumeSnapshot> {
        if (closed) return OperationResult.Failure(ProtocolError.STALE_SESSION, "controller_closed")
        val minIdx = getMediaMinVolume()
        val maxIdx = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        if (audioManager.isVolumeFixed) {
            refreshSystemVolume()
            return OperationResult.Failure(ProtocolError.UNSUPPORTED, "volume_fixed")
        }
        val targetIndex = target(minIdx, maxIdx)
        if (targetIndex !in minIdx..maxIdx) return OperationResult.Failure(ProtocolError.INVALID_ARGUMENT, "index_out_of_range")
        return try {
            audioManager.setStreamVolume(AudioManager.STREAM_MUSIC, targetIndex, 0)
            // Always read back true system state rather than assuming setStreamVolume succeeded.
            val updated = readSystemVolume(dndRestricted = false)
            state = state.copy(volume = updated)
            publish()
            publishLocalCapabilities()
            if (updated.streamIndex == targetIndex) OperationResult.Done(updated)
            else OperationResult.Failure(ProtocolError.IO_FAILURE, "volume_readback_mismatch")
        } catch (_: SecurityException) {
            val updated = readSystemVolume(dndRestricted = true)
            state = state.copy(volume = updated)
            publish()
            publishLocalCapabilities()
            OperationResult.Failure(ProtocolError.PERMISSION_REQUIRED, "dnd_policy_restriction")
        } catch (t: RuntimeException) {
            OperationResult.Failure(ProtocolError.IO_FAILURE, t.message ?: t.javaClass.simpleName)
        }
    }

    fun applyRuleVolume(percent: Int): RuleResult {
        val current = readSystemVolume(dndRestricted = false)
        if (current.isFixedVolume) return RuleResult.FIXED
        if (SystemVolumeSnapshot.indexFromPercent(percent, current.minIndex, current.maxIndex) == current.streamIndex) {
            return RuleResult.ALREADY_SET
        }
        return when (val result = setSystemMediaVolumePercent(percent)) {
            is OperationResult.Done -> RuleResult.APPLIED
            is OperationResult.Failure -> when (result.error) {
                ProtocolError.PERMISSION_REQUIRED, ProtocolError.PERMISSION_LOST -> RuleResult.PERMISSION
                ProtocolError.UNSUPPORTED -> RuleResult.FIXED
                else -> RuleResult.REJECTED
            }
            else -> RuleResult.REJECTED
        }
    }

    fun isBluetoothConnectPermissionRequired(): Boolean = Build.VERSION.SDK_INT >= 31

    fun hasBluetoothConnectPermission(): Boolean {
        if (Build.VERSION.SDK_INT < 31) return true
        return appContext.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
    }

    fun buildBluetoothSettingsIntent(): Intent =
        Intent(Settings.ACTION_BLUETOOTH_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    private fun refreshAudioDevices() {
        val rawAndroidDevices = runCatching { audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS) }
            .getOrNull() ?: return
        val defaultIds = queryDefaultMediaDeviceIds()
        val raw = ArrayList<RawOutputDevice>(rawAndroidDevices.size)
        val candidateMap = LinkedHashMap<String, AudioDeviceInfo>()

        for (d in rawAndroidDevices) {
            if (!d.isSink) continue
            val classified = classifyDevice(d, defaultIds.contains(d.id)) ?: continue
            raw += classified
            val logicalKey = when (classified.kind) {
                AudioOutputKind.SPEAKER -> AudioOutputSink.KEY_SPEAKER
                AudioOutputKind.BLUETOOTH -> AudioSinkNormalizer.bluetoothSinkKey(classified.productName, classified.address)
                AudioOutputKind.WIRED -> AudioSinkNormalizer.wiredSinkKey(classified.productName, classified.address, classified.type)
                AudioOutputKind.USB -> AudioSinkNormalizer.usbSinkKey(classified.productName, classified.address, classified.type)
                AudioOutputKind.CURRENT, AudioOutputKind.OTHER -> null
            }
            if (logicalKey != null) {
                val existing = candidateMap[logicalKey]
                if (existing == null || (isPrimaryBluetooth(d.type) && isFallbackBluetooth(existing.type))) {
                    candidateMap[logicalKey] = d
                }
            }
        }

        val normalized = AudioSinkNormalizer.normalize(
            rawDevices = raw,
            speakerLabel = speakerLabel,
            currentLabel = currentLabel
        ).filter { it.kind == AudioOutputKind.CURRENT || it.androidDeviceId > 0 }
        deviceByKey = candidateMap
        state = state.copy(outputs = normalized)
        player.onAvailableDevicesChanged(normalized)
        localPlayer.onAvailableDevicesChanged(normalized)
        if (automationMonitoring) onOutputKindsObserved(normalized.mapTo(mutableSetOf()) { it.kind })
        publish()
        if (observersActive) publishLocalCapabilities()
    }

    private fun resolveAudioDeviceInfo(targetKey: String): AudioDeviceInfo? {
        deviceByKey[targetKey]?.let { return it }
        val sink = AudioSinkNormalizer.findSink(state.outputs, targetKey) ?: return null
        return deviceByKey[sink.key]
            ?: runCatching {
                audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
                    .firstOrNull { it.isSink && it.id == sink.androidDeviceId }
            }.getOrNull()
    }

    private fun describeAudioDevice(device: AudioDeviceInfo): AudioOutputSink {
        val raw = classifyDevice(device, isDefaultMedia = false)
        if (raw == null) {
            return AudioOutputSink(
                key = "other:${device.type}:${device.id}",
                kind = AudioOutputKind.OTHER,
                androidDeviceId = device.id,
                androidDeviceType = device.type,
                name = device.productName?.toString()?.trim().orEmpty().ifEmpty { "Device #${device.id}" }
            )
        }
        val key = when (raw.kind) {
            AudioOutputKind.SPEAKER -> AudioOutputSink.KEY_SPEAKER
            AudioOutputKind.BLUETOOTH -> AudioSinkNormalizer.bluetoothSinkKey(raw.productName, raw.address)
            AudioOutputKind.WIRED -> AudioSinkNormalizer.wiredSinkKey(raw.productName, raw.address, raw.type)
            AudioOutputKind.USB -> AudioSinkNormalizer.usbSinkKey(raw.productName, raw.address, raw.type)
            AudioOutputKind.CURRENT, AudioOutputKind.OTHER -> "other:${device.type}"
        }
        val name = if (raw.kind == AudioOutputKind.SPEAKER) speakerLabel else raw.productName
        return AudioOutputSink(
            key = key,
            kind = raw.kind,
            androidDeviceId = device.id,
            androidDeviceType = device.type,
            name = name,
            address = AudioSinkNormalizer.cleanAddress(raw.address),
            isFallbackProfile = raw.isFallbackBluetooth
        )
    }

    private fun queryDefaultMediaDeviceIds(): Set<Int> {
        if (Build.VERSION.SDK_INT < 33) return emptySet()
        return runCatching {
            val attrs = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build()
            audioManager.getAudioDevicesForAttributes(attrs).map { it.id }.toSet()
        }.getOrDefault(emptySet())
    }

    private fun classifyDevice(device: AudioDeviceInfo, isDefaultMedia: Boolean): RawOutputDevice? {
        val type = device.type
        val addr = runCatching { device.address?.trim().orEmpty() }.getOrDefault("")
        val rawName = runCatching { device.productName?.toString()?.trim().orEmpty() }.getOrDefault("")
        val safeName = if (rawName.isEmpty() || rawName.equals(Build.MODEL, ignoreCase = true)) {
            ""
        } else {
            rawName
        }
        return when {
            type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> RawOutputDevice(
                id = device.id,
                type = type,
                kind = AudioOutputKind.SPEAKER,
                productName = speakerLabel,
                address = null,
                isDefaultMedia = isDefaultMedia
            )
            isPrimaryBluetooth(type) -> RawOutputDevice(
                id = device.id,
                type = type,
                kind = AudioOutputKind.BLUETOOTH,
                productName = safeName,
                address = addr,
                isPrimaryBluetooth = true,
                isDefaultMedia = isDefaultMedia
            )
            isFallbackBluetooth(type) -> RawOutputDevice(
                id = device.id,
                type = type,
                kind = AudioOutputKind.BLUETOOTH,
                productName = safeName,
                address = addr,
                isFallbackBluetooth = true,
                isDefaultMedia = isDefaultMedia
            )
            type == AudioDeviceInfo.TYPE_WIRED_HEADSET || type == AudioDeviceInfo.TYPE_WIRED_HEADPHONES -> RawOutputDevice(
                id = device.id,
                type = type,
                kind = AudioOutputKind.WIRED,
                productName = safeName.ifEmpty { "Wired audio" },
                address = addr,
                isDefaultMedia = isDefaultMedia
            )
            type == AudioDeviceInfo.TYPE_USB_DEVICE ||
                type == AudioDeviceInfo.TYPE_USB_HEADSET ||
                type == AudioDeviceInfo.TYPE_USB_ACCESSORY -> RawOutputDevice(
                id = device.id,
                type = type,
                kind = AudioOutputKind.USB,
                productName = safeName.ifEmpty { "USB audio" },
                address = addr,
                isDefaultMedia = isDefaultMedia
            )
            else -> null
        }
    }

    private fun isPrimaryBluetooth(type: Int): Boolean {
        if (type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP) return true
        if (Build.VERSION.SDK_INT >= 31 &&
            (type == AudioDeviceInfo.TYPE_BLE_HEADSET || type == AudioDeviceInfo.TYPE_BLE_SPEAKER)
        ) return true
        if (Build.VERSION.SDK_INT >= 33 && type == AudioDeviceInfo.TYPE_BLE_BROADCAST) return true
        return false
    }

    private fun isFallbackBluetooth(type: Int): Boolean = type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO

    private fun refreshSystemVolume() {
        state = state.copy(volume = readSystemVolume(dndRestricted = state.volume.dndRestricted))
        publish()
        publishLocalCapabilities()
    }

    private fun getMediaMinVolume(): Int =
        runCatching { audioManager.getStreamMinVolume(AudioManager.STREAM_MUSIC) }.getOrDefault(0)

    private fun readSystemVolume(dndRestricted: Boolean): SystemVolumeSnapshot {
        val minIdx = getMediaMinVolume()
        val maxIdx = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        val curIdx = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC)
        return SystemVolumeSnapshot(
            streamIndex = curIdx,
            minIndex = minIdx,
            maxIndex = maxIdx,
            percent = SystemVolumeSnapshot.computePercent(curIdx, minIdx, maxIdx),
            isFixedVolume = audioManager.isVolumeFixed,
            dndRestricted = dndRestricted,
            isMuted = runCatching { audioManager.isStreamMute(AudioManager.STREAM_MUSIC) }.getOrNull()
        )
    }

    private fun attachBluetoothProxiesIfPermitted() {
        val adapter = bluetoothManager?.adapter ?: return
        if (!hasBluetoothConnectPermission()) {
            if (profileListeners.isNotEmpty()) detachBluetoothProfiles()
            return
        }
        fun attach(profile: Int) {
            if (profile in profileListeners) return
            val listener = object : BluetoothProfile.ServiceListener {
                override fun onServiceConnected(profile: Int, proxy: BluetoothProfile?) {
                    if (closed || !observersActive || profileListeners[profile] !== this) {
                        if (proxy != null) runCatching { adapter.closeProfileProxy(profile, proxy) }
                        return
                    }
                    if (profile == BluetoothProfile.A2DP) a2dpProxy = proxy else leAudioProxy = proxy
                    refreshBluetooth()
                }

                override fun onServiceDisconnected(profile: Int) {
                    if (profileListeners[profile] !== this) return
                    if (profile == BluetoothProfile.A2DP) a2dpProxy = null else leAudioProxy = null
                    refreshBluetooth()
                }
            }
            profileListeners[profile] = listener
            if (!runCatching { adapter.getProfileProxy(appContext, listener, profile) }.getOrDefault(false)) {
                profileListeners.remove(profile)
            }
        }
        attach(BluetoothProfile.A2DP)
        if (Build.VERSION.SDK_INT >= 33) attach(BluetoothProfile.LE_AUDIO)
    }

    @SuppressLint("MissingPermission")
    private fun refreshBluetooth() {
        val adapter: BluetoothAdapter? = bluetoothManager?.adapter
        val required = isBluetoothConnectPermissionRequired()
        val granted = hasBluetoothConnectPermission()
        if (adapter == null) {
            state = state.copy(
                bluetooth = BluetoothSnapshot(
                    supported = false,
                    adapterEnabled = false,
                    connectPermissionRequired = required,
                    connectPermissionGranted = granted,
                    detail = "bluetooth_adapter_missing"
                )
            )
            publish()
            publishLocalCapabilities()
            return
        }

        val enabled = runCatching { adapter.isEnabled }.getOrDefault(false)
        if (!granted) {
            state = state.copy(
                bluetooth = BluetoothSnapshot(
                    supported = true,
                    adapterEnabled = enabled,
                    connectPermissionRequired = required,
                    connectPermissionGranted = false,
                    detail = "bluetooth_connect_permission_required"
                )
            )
            publish()
            publishLocalCapabilities()
            return
        }

        val bonded = runCatching {
            adapter.bondedDevices.orEmpty()
                .filter { device ->
                    val major = device.bluetoothClass?.majorDeviceClass
                    major == BluetoothClass.Device.Major.AUDIO_VIDEO || major == BluetoothClass.Device.Major.UNCATEGORIZED
                }
                .mapNotNull { it.name?.trim()?.takeIf(String::isNotEmpty) }
                .distinct()
                .sorted()
        }.getOrDefault(emptyList())

        val connectedA2dp = runCatching {
            a2dpProxy?.connectedDevices.orEmpty()
                .mapNotNull { it.name?.trim()?.takeIf(String::isNotEmpty) }
                .distinct()
                .sorted()
        }.getOrDefault(emptyList())

        val connectedLe = runCatching {
            leAudioProxy?.connectedDevices.orEmpty()
                .mapNotNull { it.name?.trim()?.takeIf(String::isNotEmpty) }
                .distinct()
                .sorted()
        }.getOrDefault(emptyList())

        state = state.copy(
            bluetooth = BluetoothSnapshot(
                supported = true,
                adapterEnabled = enabled,
                connectPermissionRequired = required,
                connectPermissionGranted = true,
                bondedAudioNames = bonded,
                connectedA2dpNames = connectedA2dp,
                connectedLeAudioNames = connectedLe,
                detail = if (enabled) "adapter_on" else "adapter_off"
            )
        )
        publish()
        publishLocalCapabilities()
    }

    private fun invalidateCatalog(names: Set<String>) {
        catalogRevision++
        catalogNeedsScan = true
        changedPackages.addAll(names)
        if (catalogNeeded) refreshAppCatalogAsync()
    }

    private fun setPublishedCatalog(status: LauncherCatalogState, catalog: AppCatalogSnapshot = AppCatalogSnapshot()) {
        if (state.catalogState == status && state.appCatalog == catalog) return
        state = state.copy(appCatalog = catalog, catalogState = status)
        publish()
        publishLocalCapabilities()
    }

    private fun refreshAppCatalogAsync() {
        if (!catalogNeeded || closed) return
        setPublishedCatalog(LauncherCatalogState.LOADING)
        if (catalogLoading) return
        catalogLoading = true
        val revision = catalogRevision
        val forceScan = catalogNeedsScan
        val changed = changedPackages.toSet()
        worker.execute {
            val catalog = runCatching {
                if (closed || revision != catalogRevision) return@runCatching null
                val pm = appContext.packageManager
                val locales = appContext.resources.configuration.locales.toLanguageTags() + "/" + android.os.LocaleList.getDefault().toLanguageTags()
                val delta = runCatching { pm.getChangedPackages(catalogIndex.sequence) }
                val changes = delta.getOrNull()?.let { LocalAppCatalog.Changes(it.sequenceNumber, it.packageNames.toSet()) }
                catalogIndex.read(locales, appContext.packageName, changes, delta.isFailure, forceScan, changed,
                    { closed || revision != catalogRevision }) {
                    val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
                    val resolved = if (Build.VERSION.SDK_INT >= 33) pm.queryIntentActivities(intent, PackageManager.ResolveInfoFlags.of(0))
                        else @Suppress("DEPRECATION") pm.queryIntentActivities(intent, 0)
                    resolved.asSequence().mapNotNull { info ->
                        val component = info.activityInfo ?: return@mapNotNull null
                        val app = component.applicationInfo ?: return@mapNotNull null
                        val pkg = app.packageName ?: return@mapNotNull null
                        val labelKey = "${info.resolvePackageName}/${info.labelRes}/${component.labelRes}/${app.labelRes}/${info.nonLocalizedLabel}/${component.nonLocalizedLabel}/${app.nonLocalizedLabel}"
                        LocalAppCatalog.Source(pkg, app.uid, app.sourceDir.orEmpty(), component.name.orEmpty(), labelKey) {
                            runCatching { info.loadLabel(pm).toString() }.getOrDefault(pkg)
                        }
                    }
                }
            }.getOrNull()

            main.post {
                catalogLoading = false
                if (closed || !catalogNeeded) return@post
                if (revision != catalogRevision) { refreshAppCatalogAsync(); return@post }
                if (catalog != null) {
                    catalogNeedsScan = false
                    changedPackages.clear()
                }
                if (catalog != null) setPublishedCatalog(LauncherCatalogState.READY, catalog)
                else setPublishedCatalog(LauncherCatalogState.FAILED)
            }
        }
    }

    private fun publishLocalCapabilities() {
        val bt = state.bluetooth
        val vol = state.volume
        val btCap = when {
            !bt.supported -> CapabilityEntry(CapabilityStatus.UNSUPPORTED, reason = "no_bluetooth_adapter", scope = "bluetooth_audio")
            !bt.connectPermissionGranted -> CapabilityEntry(
                CapabilityStatus.NEEDS_AUTHORIZATION,
                reason = "bluetooth_connect_permission",
                scope = "bluetooth_audio"
            )
            !bt.adapterEnabled -> CapabilityEntry(CapabilityStatus.UNAVAILABLE, reason = "bluetooth_off", scope = "bluetooth_audio")
            else -> CapabilityEntry(
                CapabilityStatus.AVAILABLE,
                reason = "audio_sinks_and_system_settings",
                scope = "bluetooth_audio",
                limits = mapOf("pairing" to "system_ui")
            )
        }
        val volCap = when {
            vol.isFixedVolume -> CapabilityEntry(CapabilityStatus.UNSUPPORTED, reason = "fixed_volume_device", scope = "system_media")
            vol.dndRestricted -> CapabilityEntry(CapabilityStatus.NEEDS_AUTHORIZATION, reason = "dnd_policy_restriction", scope = "system_media")
            else -> CapabilityEntry(
                CapabilityStatus.AVAILABLE,
                reason = "stream_music_readback",
                scope = "system_media",
                limits = mapOf("range" to "${vol.minIndex}..${vol.maxIndex}", "indexWrite" to "1")
            )
        }
        onLocalCapabilitiesObserved(
            mapOf(
                Capability.AUDIO_DEVICES to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "audio_device_callback",
                    scope = "local_outputs"
                ),
                Capability.PLAY_AUDIO to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "owned_audio_track",
                    scope = "owned_track",
                    limits = mapOf("pcm" to org.sonicroute.core.SrapPcmInput.FORMAT)
                ),
                Capability.ROUTE_PLAYBACK to CapabilityEntry(
                    CapabilityStatus.AVAILABLE,
                    reason = "preferred_device_with_readback",
                    scope = "owned_track"
                ),
                Capability.PHONE_VOLUME to volCap,
                Capability.BLUETOOTH_CONTROL to btCap,
                Capability.APP_CATALOG to CapabilityEntry(
                    if (state.catalogState == LauncherCatalogState.READY) CapabilityStatus.AVAILABLE else CapabilityStatus.UNAVAILABLE,
                    reason = when (state.catalogState) {
                        LauncherCatalogState.READY -> "launcher_intent_visibility"
                        LauncherCatalogState.FAILED -> "catalog_unavailable"
                        else -> "catalog_loading"
                    },
                    scope = "launcher_intents"
                )
            )
        )
    }

    private fun publish() {
        if (!closed) onStateChanged(state)
    }

    override fun close() {
        if (closed) return
        closed = true
        updateDeviceCallback()
        player.close()
        localPlayer.close()
        detachSystemObservers()
        worker.shutdownNow()
    }

    private companion object {
        const val ACTION_VOLUME_CHANGED = "android.media.VOLUME_CHANGED_ACTION"
    }
}
