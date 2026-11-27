package org.sonicroute.platform

import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.os.Build
import android.os.Binder
import android.os.Bundle
import android.os.HandlerThread
import android.os.Looper
import android.os.Process
import org.sonicroute.core.NativeProbePolicy
import java.io.File
import java.lang.reflect.InvocationTargetException
import kotlin.system.exitProcess

/**
 * Official Shizuku UserService running with Shizuku's UID (2000 Shell or 0 Root).
 * Executes strictly read-only or self-UID-scoped audio capability probes and immediately
 * restores/unregisters every temporary AudioPolicy before returning.
 */
class PrivilegedAudioProbeService : IPrivilegedAudioProbe.Stub {
    private val hostContext: Context?

    constructor(context: Context) : super() {
        this.hostContext = context
    }

    constructor() : super() {
        this.hostContext = null
    }

    override fun destroy() {
        val caller = Binder.getCallingUid()
        if (caller == 0 || caller == 2000 || isOwner(caller)) exitProcess(0)
    }

    private fun isOwner(uid: Int) = runCatching {
        val pm = hostContext?.packageManager ?: return@runCatching false
        pm.getPackagesForUid(uid)?.singleOrNull() == "org.sonicroute.app" && pm.getPackageUid("org.sonicroute.app", 0) == uid
    }.getOrDefault(false)

    override fun probeAudioCapabilities(callerAppUid: Int, selfAudioSessionId: Int): Bundle = try {
        probeOwned(callerAppUid)
    } catch (failure: NativeProbePolicy.ReleaseFailure) {
        Bundle().apply { putString(KEY_FATAL_ERROR, failure.message) }
    }

    private fun probeOwned(callerAppUid: Int): Bundle {
        val result = Bundle()
        val myUid = Process.myUid()
        val myPid = Process.myPid()
        if (myUid != 0 && myUid != 2000) {
            result.putString(KEY_FATAL_ERROR, "unexpected_service_uid:$myUid")
            return result
        }

        val context = hostContext
        if (context == null) {
            result.putString(KEY_FATAL_ERROR, "context_unavailable")
            return result
        }
        if (callerAppUid != Binder.getCallingUid() || !isOwner(callerAppUid)) {
            result.putString(KEY_FATAL_ERROR, "unverified_caller")
            return result
        }
        result.putInt(KEY_SERVICE_UID, myUid)
        result.putInt(KEY_SERVICE_PID, myPid)
        result.putString(KEY_SELINUX_CONTEXT, readSeContext())
        val audioManager = context.getSystemService(AudioManager::class.java)
        if (audioManager == null) {
            result.putString(KEY_FATAL_ERROR, "audio_manager_unavailable")
            return result
        }

        // 1. Check effective permissions of this privileged process.
        result.putBoolean(KEY_PERM_MODIFY_AUDIO_ROUTING, hasPerm(context, "android.permission.MODIFY_AUDIO_ROUTING", myPid, myUid))
        result.putBoolean(KEY_PERM_CAPTURE_AUDIO_OUTPUT, hasPerm(context, "android.permission.CAPTURE_AUDIO_OUTPUT", myPid, myUid))
        result.putBoolean(KEY_PERM_BLUETOOTH_PRIVILEGED, hasPerm(context, "android.permission.BLUETOOTH_PRIVILEGED", myPid, myUid))
        result.putBoolean(KEY_PERM_MEDIA_CONTENT_CONTROL, hasPerm(context, "android.permission.MEDIA_CONTENT_CONTROL", myPid, myUid))
        result.putBoolean(KEY_PERM_QUERY_ALL_PACKAGES, hasPerm(context, "android.permission.QUERY_ALL_PACKAGES", myPid, myUid))

        // 2. Check wide package visibility count (numeric count only).
        result.putInt(
            KEY_INSTALLED_APPS_COUNT,
            runCatching { context.packageManager.getInstalledApplications(0).size }.getOrDefault(-1)
        )

        // 3. Probe AudioPolicy RULE_MATCH_UID + ROUTE_FLAG_RENDER (strictly scoped to callerAppUid only!).
        val devices = runCatching { audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS) }.getOrNull().orEmpty()
        val speaker = devices.firstOrNull { it.isSink && it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
        val btPrimary = devices.firstOrNull {
            it.isSink && (
                it.type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP ||
                    (Build.VERSION.SDK_INT >= 31 && it.type == AudioDeviceInfo.TYPE_BLE_HEADSET) ||
                    (Build.VERSION.SDK_INT >= 31 && it.type == AudioDeviceInfo.TYPE_BLE_SPEAKER) ||
                    (Build.VERSION.SDK_INT >= 33 && it.type == AudioDeviceInfo.TYPE_BLE_BROADCAST)
                )
        }

        val renderSpeaker = if (callerAppUid > 0 && speaker != null) {
            probeAudioPolicyRender(context, audioManager, callerAppUid, speaker)
        } else {
            ProbeOutcome(false, "missing_speaker_or_uid")
        }
        result.putBoolean(KEY_UID_RENDER_SPEAKER_OK, renderSpeaker.ok)
        result.putString(KEY_UID_RENDER_SPEAKER_DETAIL, renderSpeaker.detail)

        if (callerAppUid > 0 && btPrimary != null) {
            val renderBt = probeAudioPolicyRender(context, audioManager, callerAppUid, btPrimary)
            result.putBoolean(KEY_UID_RENDER_BT_TESTED, true)
            result.putBoolean(KEY_UID_RENDER_BT_OK, renderBt.ok)
            result.putString(KEY_UID_RENDER_BT_DETAIL, renderBt.detail)
        } else {
            result.putBoolean(KEY_UID_RENDER_BT_TESTED, false)
            result.putBoolean(KEY_UID_RENDER_BT_OK, false)
            result.putString(KEY_UID_RENDER_BT_DETAIL, "no_bt_audio_connected")
        }

        // 4. Probe AudioPolicy RULE_MATCH_UID + ROUTE_FLAG_LOOP_BACK (exclusive capture + software gain path, self UID only).
        val loopback = if (callerAppUid > 0) {
            probeAudioPolicyLoopback(context, audioManager, callerAppUid)
        } else {
            ProbeOutcome(false, "invalid_caller_uid")
        }
        result.putBoolean(KEY_UID_LOOPBACK_OK, loopback.ok)
        result.putString(KEY_UID_LOOPBACK_DETAIL, loopback.detail)

        // 5. Probe AudioManager.setUidDeviceAffinity signature presence (read-only reflection).
        result.putBoolean(
            KEY_HAS_SET_UID_DEVICE_AFFINITY,
            AudioManager::class.java.declaredMethods.any { it.name == "setUidDeviceAffinity" } ||
                AudioManager::class.java.methods.any { it.name == "setUidDeviceAffinity" }
        )

        // 6. Probe active playback configurations, getClientUid, PlayerProxy.setVolume, and OEM app-volume APIs.
        probePlaybackVolumeApis(audioManager, result)

        return result
    }

    private fun hasPerm(context: Context, permission: String, pid: Int, uid: Int): Boolean {
        return runCatching {
            context.checkPermission(permission, pid, uid) == PackageManager.PERMISSION_GRANTED
        }.getOrDefault(false)
    }

    private data class ProbeOutcome(val ok: Boolean, val detail: String)

    private fun probeAudioPolicyRender(
        context: Context,
        audioManager: AudioManager,
        targetUid: Int,
        targetDevice: AudioDeviceInfo
    ): ProbeOutcome {
        val looperThread = HandlerThread("sr-probe-render").apply { start() }
        return try {
            val ruleClass = Class.forName("android.media.audiopolicy.AudioMixingRule")
            val ruleBuilderClass = Class.forName("android.media.audiopolicy.AudioMixingRule\$Builder")
            val ruleBuilder = ruleBuilderClass.getConstructor().newInstance()
            val ruleMatchUid = readIntField(ruleClass, "RULE_MATCH_UID", 4)
            ruleBuilderClass.getMethod("addMixRule", Int::class.javaPrimitiveType, Any::class.java)
                .invoke(ruleBuilder, ruleMatchUid, targetUid)
            val rule = ruleBuilderClass.getMethod("build").invoke(ruleBuilder)

            val mixClass = Class.forName("android.media.audiopolicy.AudioMix")
            val mixBuilderClass = Class.forName("android.media.audiopolicy.AudioMix\$Builder")
            val mixBuilder = mixBuilderClass.getConstructor(ruleClass).newInstance(rule)
            val routeRender = readIntField(mixClass, "ROUTE_FLAG_RENDER", 1)
            mixBuilderClass.getMethod("setRouteFlags", Int::class.javaPrimitiveType).invoke(mixBuilder, routeRender)
            mixBuilderClass.getMethod("setDevice", AudioDeviceInfo::class.java).invoke(mixBuilder, targetDevice)
            mixBuilderClass.getMethod("setFormat", AudioFormat::class.java).invoke(
                mixBuilder,
                AudioFormat.Builder()
                    .setSampleRate(48_000)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build()
            )
            val mix = mixBuilderClass.getMethod("build").invoke(mixBuilder)
                ?: return ProbeOutcome(false, "audio_mix_build_null")

            val policyClass = Class.forName("android.media.audiopolicy.AudioPolicy")
            val policyBuilderClass = Class.forName("android.media.audiopolicy.AudioPolicy\$Builder")
            val policyBuilder = policyBuilderClass.getConstructor(Context::class.java).newInstance(context)
            policyBuilderClass.getMethod("setLooper", Looper::class.java).invoke(policyBuilder, looperThread.looper)
            policyBuilderClass.getMethod("addMix", mixClass).invoke(policyBuilder, mix)
            val policy = policyBuilderClass.getMethod("build").invoke(policyBuilder)
                ?: return ProbeOutcome(false, "audio_policy_build_null")

            inspectPolicy(audioManager, policyClass, policy) { status ->
                if (status == 0) ProbeOutcome(true, "registered_and_released(type=${targetDevice.type},id=${targetDevice.id})")
                else ProbeOutcome(false, "registerAudioPolicy_rejected=$status(type=${targetDevice.type})")
            }
        } catch (failure: NativeProbePolicy.ReleaseFailure) {
            throw failure
        } catch (t: Throwable) {
            ProbeOutcome(false, describeError(t))
        } finally {
            runCatching { looperThread.quitSafely() }
        }
    }

    private fun probeAudioPolicyLoopback(
        context: Context,
        audioManager: AudioManager,
        targetUid: Int
    ): ProbeOutcome {
        val looperThread = HandlerThread("sr-probe-loopback").apply { start() }
        return try {
            val ruleClass = Class.forName("android.media.audiopolicy.AudioMixingRule")
            val ruleBuilderClass = Class.forName("android.media.audiopolicy.AudioMixingRule\$Builder")
            val ruleBuilder = ruleBuilderClass.getConstructor().newInstance()
            runCatching {
                val setRole = ruleBuilderClass.getMethod("setTargetMixRole", Int::class.javaPrimitiveType)
                setRole.invoke(ruleBuilder, readIntField(ruleClass, "MIX_ROLE_PLAYERS", 0))
            }
            val ruleMatchUid = readIntField(ruleClass, "RULE_MATCH_UID", 4)
            ruleBuilderClass.getMethod("addMixRule", Int::class.javaPrimitiveType, Any::class.java)
                .invoke(ruleBuilder, ruleMatchUid, targetUid)
            val rule = ruleBuilderClass.getMethod("build").invoke(ruleBuilder)

            val mixClass = Class.forName("android.media.audiopolicy.AudioMix")
            val mixBuilderClass = Class.forName("android.media.audiopolicy.AudioMix\$Builder")
            val mixBuilder = mixBuilderClass.getConstructor(ruleClass).newInstance(rule)
            val routeLoopback = readIntField(mixClass, "ROUTE_FLAG_LOOP_BACK", 2)
            mixBuilderClass.getMethod("setRouteFlags", Int::class.javaPrimitiveType).invoke(mixBuilder, routeLoopback)
            mixBuilderClass.getMethod("setFormat", AudioFormat::class.java).invoke(
                mixBuilder,
                AudioFormat.Builder()
                    .setSampleRate(48_000)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                    .build()
            )
            val mix = mixBuilderClass.getMethod("build").invoke(mixBuilder)
                ?: return ProbeOutcome(false, "audio_mix_build_null")

            val policyClass = Class.forName("android.media.audiopolicy.AudioPolicy")
            val policyBuilderClass = Class.forName("android.media.audiopolicy.AudioPolicy\$Builder")
            val policyBuilder = policyBuilderClass.getConstructor(Context::class.java).newInstance(context)
            policyBuilderClass.getMethod("setLooper", Looper::class.java).invoke(policyBuilder, looperThread.looper)
            policyBuilderClass.getMethod("addMix", mixClass).invoke(policyBuilder, mix)
            val policy = policyBuilderClass.getMethod("build").invoke(policyBuilder)
                ?: return ProbeOutcome(false, "audio_policy_build_null")

            inspectPolicy(audioManager, policyClass, policy) { status ->
                if (status != 0) ProbeOutcome(false, "registerAudioPolicy_loopback_rejected=$status")
                else inspectOwnedLoopback(context, mixClass, mix, policyClass, policy)
            }
        } catch (failure: NativeProbePolicy.ReleaseFailure) {
            throw failure
        } catch (t: Throwable) {
            ProbeOutcome(false, describeError(t))
        } finally {
            runCatching { looperThread.quitSafely() }
        }
    }

    private fun inspectOwnedLoopback(context: Context, mixClass: Class<*>, mix: Any, policyClass: Class<*>, policy: Any): ProbeOutcome {
        val regAddr = runCatching { mixClass.getMethod("getRegistration").invoke(mix) as? String }.getOrNull().orEmpty()
        val rec = policyClass.getMethod("createAudioRecordSink", mixClass).invoke(policy, mix) as? AudioRecord
        return if (rec == null) {
            val fallback = if (regAddr.isNotEmpty()) probeRemoteSubmixFallback(context, regAddr) else "no_reg_addr"
            ProbeOutcome(fallback == "initialized", "sink_null(submix=$fallback)")
        } else try {
            val initialized = rec.state == AudioRecord.STATE_INITIALIZED
            ProbeOutcome(initialized, if (initialized) "sink_initialized" else "sink_state_${rec.state}")
        } finally { rec.release() }
    }

    private fun probeRemoteSubmixFallback(context: Context, regAddr: String): String {
        var rec: AudioRecord? = null
        return try {
            val minBuf = AudioRecord.getMinBufferSize(
                48_000,
                AudioFormat.CHANNEL_IN_STEREO,
                AudioFormat.ENCODING_PCM_16BIT
            ).coerceAtLeast(4096)
            val attrBuilder = AudioAttributes.Builder()
            AudioAttributes.Builder::class.java
                .getMethod("setInternalCapturePreset", Int::class.javaPrimitiveType)
                .invoke(attrBuilder, android.media.MediaRecorder.AudioSource.REMOTE_SUBMIX)
            AudioAttributes.Builder::class.java
                .getMethod("addTag", String::class.java)
                .invoke(attrBuilder, "addr=$regAddr")
            val attrs = attrBuilder.build()
            val format = AudioFormat.Builder()
                .setSampleRate(48_000)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build()
            val builder = AudioRecord.Builder()
                .setAudioFormat(format)
                .setBufferSizeInBytes(minBuf * 2)
            AudioRecord.Builder::class.java
                .getMethod("setAudioAttributes", AudioAttributes::class.java)
                .invoke(builder, attrs)
            if (Build.VERSION.SDK_INT >= 31) {
                runCatching { builder.setContext(context) }
            }
            val built = builder.build()
            rec = built
            if (built.state == AudioRecord.STATE_INITIALIZED) "initialized" else "state_${built.state}"
        } catch (t: Throwable) {
            "err:${describeError(t)}"
        } finally {
            runCatching { rec?.release() }
        }
    }

    private fun inspectPolicy(audio: AudioManager, policyClass: Class<*>, policy: Any, sample: (Int) -> ProbeOutcome): ProbeOutcome {
        val manager = AudioManager::class.java
        val register = manager.getMethod("registerAudioPolicy", policyClass)
        val release = runCatching {
            val unregister = manager.getMethod("unregisterAudioPolicy", policyClass)
            val status = policyClass.getMethod("getStatus")
            val unregistered = policyClass.getField("POLICY_STATUS_UNREGISTERED").getInt(null)
            val checkedRelease: () -> Boolean = {
                unregister.invoke(audio, policy)
                status.invoke(policy) == unregistered
            }
            checkedRelease
        }.getOrNull()
        return NativeProbePolicy.inspect({ register.invoke(audio, policy) as Int }, release, sample)
    }

    private fun probePlaybackVolumeApis(
        audioManager: AudioManager,
        out: Bundle
    ) {
        val configs = runCatching { audioManager.activePlaybackConfigurations }.getOrElse { err ->
            out.putInt(KEY_ACTIVE_PLAYBACK_COUNT, -1)
            out.putString(KEY_PLAYER_PROXY_DETAIL, "activePlayback_error:${describeError(err)}")
            return
        }
        out.putInt(KEY_ACTIVE_PLAYBACK_COUNT, configs.size)

        var visibleUidCount = 0
        var nonNullProxyCount = 0
        var hasSetVolumeMethod = false

        for (cfg in configs) {
            val clientUid = runCatching {
                val m = cfg.javaClass.getMethod("getClientUid")
                (m.invoke(cfg) as Number).toInt()
            }.getOrDefault(-1)
            if (clientUid > 0) visibleUidCount++

            val proxy = runCatching {
                val m = cfg.javaClass.getMethod("getPlayerProxy")
                m.invoke(cfg)
            }.getOrNull()
            if (proxy != null) {
                nonNullProxyCount++
                val setVol = runCatching {
                    proxy.javaClass.getMethod("setVolume", Float::class.javaPrimitiveType)
                }.getOrNull()
                if (setVol != null) hasSetVolumeMethod = true
            }
        }

        // Read-only signature scan for OEM per-app volume methods on AudioManager (e.g., MIUI/HyperOS/Samsung).
        val oemMethods = AudioManager::class.java.declaredMethods
            .map { it.name }
            .filter { name ->
                val lower = name.lowercase()
                lower.contains("appvolume") || lower.contains("uidvolume") ||
                    lower.contains("multiaudio") || lower.contains("appmute") ||
                    lower.contains("soundassist")
            }
            .distinct()
            .sorted()

        out.putInt(KEY_VISIBLE_PLAYBACK_UID_COUNT, visibleUidCount)
        out.putInt(KEY_VISIBLE_PLAYER_PROXY_COUNT, nonNullProxyCount)
        out.putBoolean(KEY_PLAYER_PROXY_SET_VOLUME_API, hasSetVolumeMethod)
        out.putBoolean(KEY_SELF_PLAYER_PROXY_TESTED_OK, false)
        out.putString(KEY_PLAYER_PROXY_DETAIL, "gain_write_skipped_unverified_restore")
        out.putString(KEY_OEM_APP_VOLUME_METHODS, oemMethods.joinToString(","))
    }

    private fun readIntField(owner: Class<*>, name: String, fallback: Int): Int {
        return runCatching { owner.getField(name).getInt(null) }.getOrDefault(fallback)
    }

    private fun readSeContext(): String {
        return runCatching {
            File("/proc/self/attr/current").readText(Charsets.US_ASCII)
                .trim('\u0000', '\n', '\r', ' ')
                .take(128)
        }.getOrDefault("unknown")
    }

    private fun describeError(t: Throwable): String {
        val cause = if (t is InvocationTargetException && t.cause != null) t.cause!! else t
        val msg = cause.message?.take(120).orEmpty()
        return if (msg.isEmpty()) cause.javaClass.simpleName else "${cause.javaClass.simpleName}:$msg"
    }

    companion object {
        const val KEY_SERVICE_UID = "service_uid"
        const val KEY_SERVICE_PID = "service_pid"
        const val KEY_SELINUX_CONTEXT = "selinux_context"
        const val KEY_FATAL_ERROR = "fatal_error"
        const val KEY_PERM_MODIFY_AUDIO_ROUTING = "perm_modify_audio_routing"
        const val KEY_PERM_CAPTURE_AUDIO_OUTPUT = "perm_capture_audio_output"
        const val KEY_PERM_BLUETOOTH_PRIVILEGED = "perm_bluetooth_privileged"
        const val KEY_PERM_MEDIA_CONTENT_CONTROL = "perm_media_content_control"
        const val KEY_PERM_QUERY_ALL_PACKAGES = "perm_query_all_packages"
        const val KEY_INSTALLED_APPS_COUNT = "installed_apps_count"
        const val KEY_UID_RENDER_SPEAKER_OK = "uid_render_speaker_ok"
        const val KEY_UID_RENDER_SPEAKER_DETAIL = "uid_render_speaker_detail"
        const val KEY_UID_RENDER_BT_TESTED = "uid_render_bt_tested"
        const val KEY_UID_RENDER_BT_OK = "uid_render_bt_ok"
        const val KEY_UID_RENDER_BT_DETAIL = "uid_render_bt_detail"
        const val KEY_UID_LOOPBACK_OK = "uid_loopback_ok"
        const val KEY_UID_LOOPBACK_DETAIL = "uid_loopback_detail"
        const val KEY_HAS_SET_UID_DEVICE_AFFINITY = "has_set_uid_device_affinity"
        const val KEY_ACTIVE_PLAYBACK_COUNT = "active_playback_count"
        const val KEY_VISIBLE_PLAYBACK_UID_COUNT = "visible_playback_uid_count"
        const val KEY_VISIBLE_PLAYER_PROXY_COUNT = "visible_player_proxy_count"
        const val KEY_PLAYER_PROXY_SET_VOLUME_API = "player_proxy_set_volume_api"
        const val KEY_SELF_PLAYER_PROXY_TESTED_OK = "self_player_proxy_tested_ok"
        const val KEY_PLAYER_PROXY_DETAIL = "player_proxy_detail"
        const val KEY_OEM_APP_VOLUME_METHODS = "oem_app_volume_methods"
    }
}
