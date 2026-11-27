package org.sonicroute.platform

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioPlaybackConfiguration
import android.os.Binder
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.Looper
import android.os.Process
import android.os.UserHandle
import org.sonicroute.core.NativeAppRouteEvidence
import org.sonicroute.core.NativeAppRoutingPipe
import org.sonicroute.core.NativeFlingerTrack
import java.io.File
import java.util.concurrent.TimeUnit
import kotlin.system.exitProcess

/** User-started Shizuku service for one physical render policy. The real process identity must have MODIFY_AUDIO_ROUTING. */
class NativeAppRoutingService(private val context: Context?) : INativeAppRouting.Stub() {
    constructor() : this(null)
    private val thread = HandlerThread("SonicRoute-app-route").apply { start() }
    private val handler = Handler(thread.looper)
    internal val callbackLooper: Looper get() = thread.looper
    internal fun expireDirectLease() { finish("released", "direct_lease_expired") }
    private var route: Route? = null
    private class Denied(val reason: String) : Exception()
    private class Route(val pkg: String, val uid: Int, val target: AudioDeviceInfo, val audio: AudioManager,
        val observer: INativeAppRouteObserver, val evidence: NativeAppRouteEvidence, val nativeSessions: Set<Int>) {
        var unregister: (() -> Unit)? = null
        var verified = false
        var restoring = false
    }
    private val timeout = Runnable { checkRoute(deadline = true) }
    private val death = IBinder.DeathRecipient { handler.post { finish("released", "owner_closed") } }
    private val playback = object : AudioManager.AudioPlaybackCallback() {
        override fun onPlaybackConfigChanged(configs: MutableList<AudioPlaybackConfiguration>?) = checkRoute()
    }
    private val devices = object : AudioDeviceCallback() {
        override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>?) { checkRoute() }
        override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>?) { checkRoute() }
    }
    private val packages = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.data?.schemeSpecificPart == route?.pkg) finish("released", "app_removed")
        }
    }

    override fun applyRoute(packageName: String, expectedAppUid: Int, expectedSigner: String, targetDeviceId: Int, expectedType: Int,
                            expectedAddress: String, observer: INativeAppRouteObserver) {
        applyOwned(Binder.getCallingUid(), packageName, expectedAppUid, expectedSigner, targetDeviceId, expectedType, expectedAddress, observer)
    }

    internal fun applyOwned(caller: Int, packageName: String, expectedAppUid: Int, expectedSigner: String, targetDeviceId: Int, expectedType: Int,
                            expectedAddress: String, observer: INativeAppRouteObserver) {
        if (!owner(caller)) { notify(observer, "error", "unverified_caller"); return }
        handler.post {
            if (route != null) { notify(observer, "error", "route_busy"); return@post }
            try {
                val ctx = context ?: throw Denied("backend_context_unavailable")
                if (Process.myUid() !in setOf(0, 2000) ||
                    ctx.checkPermission("android.permission.MODIFY_AUDIO_ROUTING", Process.myPid(), Process.myUid()) != PackageManager.PERMISSION_GRANTED
                ) throw Denied("routing_permission_required")
                val pm = ctx.packageManager
                val uid = pm.getApplicationInfo(packageName, 0).uid
                if (uid != expectedAppUid || uid == caller || UserHandle.getUserHandleForUid(uid) != UserHandle.getUserHandleForUid(caller) ||
                    uid % 100000 !in Process.FIRST_APPLICATION_UID..Process.LAST_APPLICATION_UID ||
                    pm.getPackagesForUid(uid)?.singleOrNull() != packageName || NativeAppRoutingIdentity.packageSigner(ctx, packageName) != expectedSigner
                ) throw Denied("ambiguous_app_identity")
                val audio = ctx.getSystemService(AudioManager::class.java) ?: throw Denied("audio_service_unavailable")
                val ports = audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS).filter(::physical)
                val target = target(ports, expectedType, expectedAddress, targetDeviceId) ?: throw Denied("target_disconnected")
                val players = tracks(audio, uid, verifyNativeUsage = true)
                val before = players.associate { it.id to it.outputs }
                if (before.isEmpty()) throw Denied("no_active_media")
                val physicalIds = ports.map { it.id }.toSet()
                if (before.values.any { it.isEmpty() || !physicalIds.containsAll(it) }) throw Denied("device_readback_unavailable")
                val evidence = NativeAppRouteEvidence(before, target.id)
                if (evidence.isOnTarget(before)) { notify(observer, "released", "already_on_output", before, before); return@post }
                val current = Route(packageName, uid, target, audio, observer, evidence,
                    players.filter { it.nativeOutput }.map { it.session }.toSet())
                route = current
                observer.asBinder().linkToDeath(death, 0)
                audio.registerAudioPlaybackCallback(playback, handler)
                audio.registerAudioDeviceCallback(devices, handler)
                val filter = IntentFilter(Intent.ACTION_PACKAGE_REMOVED).apply { addDataScheme("package") }
                if (Build.VERSION.SDK_INT >= 33) ctx.registerReceiver(packages, filter, null, handler, Context.RECEIVER_NOT_EXPORTED)
                else ctx.registerReceiver(packages, filter, null, handler)
                current.unregister = registerPhysicalRender(ctx, audio, uid, target, players)
                handler.postDelayed(timeout, 2500)
                checkRoute()
            } catch (e: Exception) {
                val reason = (e as? Denied)?.reason ?: "platform_route_unavailable"
                if (route == null) notify(observer, "error", reason) else finish("error", reason)
            }
        }
    }

    override fun restoreRoute(observer: INativeAppRouteObserver) {
        restoreOwned(Binder.getCallingUid(), observer)
    }

    internal fun restoreOwned(caller: Int, observer: INativeAppRouteObserver) {
        if (!owner(caller)) return
        handler.post {
            val current = route ?: return@post
            if (current.observer.asBinder() != observer.asBinder() || current.restoring) return@post
            current.restoring = true
            try {
                current.unregister?.invoke()
                current.unregister = null
                handler.removeCallbacks(timeout)
                handler.postDelayed(timeout, 2500)
                checkRoute()
            } catch (_: Exception) { finish("error", "policy_release_failed") }
        }
    }

    private fun owner(caller: Int) = runCatching {
        val pm = context?.packageManager ?: return@runCatching false
        pm.getPackageUid(NativeAppRoutingPipe.OWNER, 0) == caller && pm.getPackagesForUid(caller)?.singleOrNull() == NativeAppRoutingPipe.OWNER
    }.getOrDefault(false)

    private fun target(ports: List<AudioDeviceInfo>, type: Int, address: String, id: Int): AudioDeviceInfo? {
        // AudioMix retains type and address. Two live ports with that pair cannot be selected independently.
        return ports.singleOrNull {
            it.type == type && it.address.orEmpty() == address
        }?.takeIf { it.id == id }
    }

    private fun checkRoute(deadline: Boolean = false) {
        val r = route ?: return
        try {
            if (context?.packageManager?.getPackagesForUid(r.uid)?.singleOrNull() != r.pkg) { finish("released", "app_removed"); return }
            val ports = r.audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS).filter(::physical)
            if (target(ports, r.target.type, r.target.address.orEmpty(), r.target.id) == null) {
                finish("released", "target_disconnected"); return
            }
            var now = tracks(r.audio, r.uid).associate { it.id to it.outputs }
            val confirming = deadline || (!r.verified && r.evidence.isOnTarget(now)) || (r.restoring && r.evidence.isRestored(now))
            if (confirming && r.nativeSessions.isNotEmpty()) {
                now = tracks(r.audio, r.uid, requireNative = r.nativeSessions).associate { it.id to it.outputs }
            }
            when {
                !r.evidence.hasOriginalPlayer(now) -> {
                    // A native output handover can briefly pause the original player. Accept only
                    // the same player returning during the already scheduled verification window.
                    if (deadline || r.verified && !r.restoring) finish("released", "playback_ended")
                }
                r.restoring && r.evidence.isRestored(now) -> finish("restored", "original_output_verified", now)
                r.restoring && deadline -> finish("released", "policy_released_readback_changed", now)
                r.restoring -> Unit
                r.evidence.isOnTarget(now) -> if (!r.verified) {
                    handler.removeCallbacks(timeout)
                    r.verified = true
                    notify(r.observer, "verified", "physical_output_verified", r.evidence.original, now)
                } else Unit
                r.verified || deadline -> finish("error", "target_readback_failed", now)
                else -> Unit
            }
        } catch (_: Exception) { finish("error", "device_readback_unavailable") }
    }

    private fun finish(phase: String, reason: String, now: Map<Int, Set<Int>> = emptyMap()) {
        val r = route ?: return
        route = null
        handler.removeCallbacks(timeout)
        val release = runCatching { r.unregister?.invoke() }
        runCatching { r.audio.unregisterAudioPlaybackCallback(playback) }
        runCatching { r.audio.unregisterAudioDeviceCallback(devices) }
        runCatching { context?.unregisterReceiver(packages) }
        runCatching { r.observer.asBinder().unlinkToDeath(death, 0) }
        notify(r.observer, if (release.isFailure) "error" else phase, if (release.isFailure) "policy_release_failed" else reason,
            r.evidence.original, now)
    }

    private data class Player(val id: Int, val session: Int, val usage: Int, val legacy: Boolean, val outputs: Set<Int>,
                              val nativeOutput: Boolean = false)

    private fun tracks(audio: AudioManager, uid: Int, verifyNativeUsage: Boolean = false, requireNative: Set<Int> = emptySet()): List<Player> {
        val cls = AudioPlaybackConfiguration::class.java
        val getUid = cls.getMethod("getClientUid")
        val getState = cls.getMethod("getPlayerState")
        val getId = cls.getMethod("getPlayerInterfaceId")
        val getSession = cls.getMethod("getSessionId")
        val outputs = runCatching { cls.getMethod("getAudioDeviceInfos") }.getOrNull()
        val single = if (outputs == null) runCatching { cls.getMethod("getAudioDeviceInfo") }.getOrNull() else null
        val active = cls.getField("PLAYER_STATE_STARTED").getInt(null)
        val candidates = buildList {
            for (config in audio.activePlaybackConfigurations) {
                if (getUid.invoke(config) != uid || getState.invoke(config) != active) continue
                val usage = config.audioAttributes.usage
                val session = getSession.invoke(config) as Int
                if (usage != AudioAttributes.USAGE_MEDIA && (usage != AudioAttributes.USAGE_UNKNOWN || session <= 0)) continue
                val id = getId.invoke(config) as Int
                if (id <= 0) throw Denied("device_readback_unavailable")
                val ids = runCatching {
                    if (outputs != null) (outputs.invoke(config) as List<*>).filterIsInstance<AudioDeviceInfo>().map { it.id }.toSet()
                    else listOfNotNull(single?.invoke(config) as? AudioDeviceInfo).map { it.id }.toSet()
                }.getOrDefault(emptySet())
                add(Player(id, session, usage, usage == AudioAttributes.USAGE_UNKNOWN, ids) to config.audioAttributes)
            }
        }
        val need = candidates.map { it.first }.filter { it.outputs.isEmpty() || verifyNativeUsage && it.legacy || it.session in requireNative }
            .map { it.session }.filter { it > 0 }.toSet()
        val flinger = if (need.isEmpty()) emptyMap() else readFlinger(uid, need)
        val ports = if (flinger.isEmpty()) emptyList() else audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS).filter(::physical)
        return candidates.mapNotNull { (player, attributes) ->
            val rows = flinger[player.session].orEmpty()
            if (player.legacy && (rows.any { it.streamType != AudioManager.STREAM_MUSIC || it.usage !in 0..1 } ||
                    (rows.isEmpty() && !legacyMusic(attributes)))) return@mapNotNull null
            val actualUsage = if (player.legacy && rows.isNotEmpty()) rows.map { it.usage }.distinct().singleOrNull() ?: return@mapNotNull null
                else player.usage
            val nativeNames = rows.flatMap { it.physicalOutputs }.toSet()
            val matchedIds = nativeNames.map { type ->
                val code = flingerType(type)
                ports.singleOrNull { it.type == code }?.id
            }
            val nativeIds = if (matchedIds.isNotEmpty() && null !in matchedIds) matchedIds.filterNotNull().toSet() else emptySet()
            val actual = NativeFlingerTrack.confirmOutputs(player.outputs, nativeIds, player.session in requireNative)
            player.copy(usage = actualUsage, outputs = actual, nativeOutput = nativeIds.isNotEmpty() && actual == nativeIds)
        }
    }

    private fun readFlinger(uid: Int, sessions: Set<Int>): Map<Int, List<NativeFlingerTrack>> {
        if (context?.checkPermission("android.permission.DUMP", Process.myPid(), Process.myUid()) != PackageManager.PERMISSION_GRANTED) return emptyMap()
        return runCatching {
            val child = ProcessBuilder("/system/bin/dumpsys", "-t", "1", "media.audio_flinger").redirectError(File("/dev/null")).start()
            try {
                child.outputStream.close()
                val result = child.inputStream.reader().use { NativeFlingerTrack.read(it, uid, sessions) }
                if (child.waitFor(1, TimeUnit.SECONDS) && child.exitValue() == 0) result else emptyMap()
            } finally { child.destroyForcibly() }
        }.getOrDefault(emptyMap())
    }

    private fun flingerType(name: String) = when (name) {
        "AUDIO_DEVICE_OUT_EARPIECE" -> AudioDeviceInfo.TYPE_BUILTIN_EARPIECE
        "AUDIO_DEVICE_OUT_SPEAKER" -> AudioDeviceInfo.TYPE_BUILTIN_SPEAKER
        "AUDIO_DEVICE_OUT_WIRED_HEADPHONE" -> AudioDeviceInfo.TYPE_WIRED_HEADPHONES
        "AUDIO_DEVICE_OUT_WIRED_HEADSET" -> AudioDeviceInfo.TYPE_WIRED_HEADSET
        "AUDIO_DEVICE_OUT_BLUETOOTH_A2DP", "AUDIO_DEVICE_OUT_BLUETOOTH_A2DP_HEADPHONES", "AUDIO_DEVICE_OUT_BLUETOOTH_A2DP_SPEAKER" -> AudioDeviceInfo.TYPE_BLUETOOTH_A2DP
        "AUDIO_DEVICE_OUT_BLE_HEADSET" -> AudioDeviceInfo.TYPE_BLE_HEADSET
        "AUDIO_DEVICE_OUT_BLE_SPEAKER" -> AudioDeviceInfo.TYPE_BLE_SPEAKER
        "AUDIO_DEVICE_OUT_USB_HEADSET" -> AudioDeviceInfo.TYPE_USB_HEADSET
        "AUDIO_DEVICE_OUT_USB_DEVICE" -> AudioDeviceInfo.TYPE_USB_DEVICE
        "AUDIO_DEVICE_OUT_HDMI" -> AudioDeviceInfo.TYPE_HDMI
        else -> AudioDeviceInfo.TYPE_UNKNOWN
    }

    private fun legacyMusic(attributes: AudioAttributes): Boolean = runCatching {
        AudioAttributes::class.java.getMethod("toLegacyStreamType", AudioAttributes::class.java).invoke(null, attributes) == AudioManager.STREAM_MUSIC
    }.getOrDefault(false)

    private fun registerPhysicalRender(ctx: Context, audio: AudioManager, uid: Int, device: AudioDeviceInfo, players: List<Player>): () -> Unit {
        val ruleType = Class.forName("android.media.audiopolicy.AudioMixingRule")
        val mixType = Class.forName("android.media.audiopolicy.AudioMix")
        val policyType = Class.forName("android.media.audiopolicy.AudioPolicy")
        val pb = Class.forName("android.media.audiopolicy.AudioPolicy\$Builder").getConstructor(Context::class.java).newInstance(ctx)
        pb.javaClass.getMethod("setLooper", Looper::class.java).invoke(pb, thread.looper)
        val renderFormat = AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(48000).setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).build()
        for ((usage, matching) in players.groupBy { it.usage }) {
            val rules = Class.forName("android.media.audiopolicy.AudioMixingRule\$Builder").getConstructor().newInstance()
            val add = rules.javaClass.getMethod("addMixRule", Int::class.javaPrimitiveType, Any::class.java)
            add.invoke(rules, ruleType.getField("RULE_MATCH_UID").getInt(null), uid)
            add.invoke(rules, ruleType.getField("RULE_MATCH_ATTRIBUTE_USAGE").getInt(null), AudioAttributes.Builder().setUsage(usage).build())
            if (usage == AudioAttributes.USAGE_UNKNOWN) {
                val sessionRule = ruleType.getField("RULE_MATCH_AUDIO_SESSION_ID").getInt(null)
                matching.map { it.session }.distinct().forEach { add.invoke(rules, sessionRule, it) }
            }
            val builder = Class.forName("android.media.audiopolicy.AudioMix\$Builder").getConstructor(ruleType)
                .newInstance(rules.javaClass.getMethod("build").invoke(rules))
            builder.javaClass.getMethod("setRouteFlags", Int::class.javaPrimitiveType).invoke(builder, mixType.getField("ROUTE_FLAG_RENDER").getInt(null))
            builder.javaClass.getMethod("setDevice", AudioDeviceInfo::class.java).invoke(builder, device)
            builder.javaClass.getMethod("setFormat", AudioFormat::class.java).invoke(builder, renderFormat)
            pb.javaClass.getMethod("addMix", mixType).invoke(pb, builder.javaClass.getMethod("build").invoke(builder))
        }
        val policy = pb.javaClass.getMethod("build").invoke(pb)
        val unregister = AudioManager::class.java.getMethod("unregisterAudioPolicy", policyType)
        val register = AudioManager::class.java.getMethod("registerAudioPolicy", policyType)
        if (register.invoke(audio, policy) != 0) throw Denied("policy_registration_refused")
        return { unregister.invoke(audio, policy); Unit }
    }

    internal fun shutdown(after: () -> Unit) {
        handler.post { try { finish("released", "backend_closed") } finally { thread.quitSafely(); after() } }
    }

    override fun destroy() {
        val caller = Binder.getCallingUid()
        if (caller != 0 && caller != 2000 && !owner(caller)) return
        shutdown { exitProcess(0) }
    }

    companion object {
        private fun notify(observer: INativeAppRouteObserver, phase: String, reason: String,
                           original: Map<Int, Set<Int>> = emptyMap(), actual: Map<Int, Set<Int>> = emptyMap()) {
            runCatching { observer.routeState(phase, reason, original.values.flatten().distinct().sorted().toIntArray(),
                actual.values.flatten().distinct().sorted().toIntArray()) }
        }
        fun physical(device: AudioDeviceInfo): Boolean = device.isSink && when (device.type) {
            AudioDeviceInfo.TYPE_BUILTIN_EARPIECE, AudioDeviceInfo.TYPE_BUILTIN_SPEAKER, AudioDeviceInfo.TYPE_WIRED_HEADPHONES,
            AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_BLUETOOTH_A2DP, AudioDeviceInfo.TYPE_USB_DEVICE,
            AudioDeviceInfo.TYPE_USB_ACCESSORY, AudioDeviceInfo.TYPE_USB_HEADSET, AudioDeviceInfo.TYPE_HDMI, AudioDeviceInfo.TYPE_HDMI_ARC,
            AudioDeviceInfo.TYPE_BLE_HEADSET, AudioDeviceInfo.TYPE_BLE_SPEAKER, AudioDeviceInfo.TYPE_HEARING_AID -> true
            else -> false
        }
    }
}
