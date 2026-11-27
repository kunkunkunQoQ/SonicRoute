package org.sonicroute.platform

import android.bluetooth.BluetoothManager
import android.Manifest
import android.content.pm.PackageManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.media.AudioPlaybackConfiguration
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.Process
import android.os.UserHandle
import android.os.UserManager
import org.json.JSONObject
import org.sonicroute.core.AppCatalogEntry
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendStatus
import org.sonicroute.core.NativeAppRoutingPipe
import org.sonicroute.core.NativeSavedRoute
import org.sonicroute.core.PermissionSnapshot

data class NativeRouteRuleState(val saved: List<NativeSavedRoute>, val armed: String?)

/** Persistent choices have no authority. A phone-local activation arms one choice for this backend generation. */
internal class NativeAppRoutingRules(private val context: Context, private val routing: NativeAppRouting,
                                    private val permissions: () -> PermissionSnapshot, private val changed: () -> Unit) {
    private val prefs = context.getSharedPreferences("native_app_output_choices", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private val audio = context.getSystemService(AudioManager::class.java)
    private val userSerial = context.getSystemService(UserManager::class.java).getSerialNumberForUser(Process.myUserHandle())
    private val saved = LinkedHashMap<String, NativeSavedRoute>()
    private var armed: NativeSavedRoute? = null
    private var generation = -1L
    private var backend = Backend.STANDARD
    private var lastTracks: Set<AudioPlaybackConfiguration>? = null
    private var lastOutputs: List<NativeAppOutput>? = null
    private var ownsRoute = false
    private val check = Runnable { evaluate() }
    private val expire = Runnable { disarm(true) }
    val active get() = armed != null
    val state get() = NativeRouteRuleState(saved.values.toList(), armed?.packageName)

    init {
        for ((key, value) in prefs.all) {
            runCatching {
                val row = JSONObject(value as String)
                val rule = NativeSavedRoute(key, row.getString("app"), row.getString("label"), row.getLong("user"),
                    row.getString("signer"), row.getString("output"))
                if (NativeAppRoutingPipe.validPackage(key) && rule.userSerial == userSerial && rule.signer.matches(Regex("[0-9a-f]{64}"))) saved[key] = rule
            }
        }
    }

    private val playback = object : AudioManager.AudioPlaybackCallback() {
        override fun onPlaybackConfigChanged(configs: MutableList<AudioPlaybackConfiguration>?) {
            val media = configs.orEmpty().filter(::mediaCandidate).toSet()
            if (media != lastTracks) { lastTracks = media; schedule() }
        }
    }
    private val packages = object : BroadcastReceiver() {
        override fun onReceive(ctx: Context?, intent: Intent?) {
            val pkg = intent?.data?.schemeSpecificPart ?: return
            if (pkg != armed?.packageName) return
            if (intent.action == Intent.ACTION_PACKAGE_REMOVED && !intent.getBooleanExtra(Intent.EXTRA_REPLACING, false)) {
                remove(pkg)
            } else if (identity(pkg)?.second != armed?.signer) disarm(true)
            else schedule()
        }
    }

    fun save(app: AppCatalogEntry, output: NativeAppOutput, outputLabel: String): Boolean {
        val (uid, signer) = identity(app.packageName) ?: return false
        if (app.isSelf || uid != app.uid || userSerial < 0) return false
        val devices = routing.physicalOutputs()
        if (output !in devices) return false
        val key = stableKey(output) ?: return false
        if (devices.count { stableKey(it) == key } != 1) return false
        val rule = NativeSavedRoute(app.packageName, app.label.take(160), outputLabel.take(160), userSerial, signer, key)
        val record = JSONObject().put("app", rule.appLabel).put("label", rule.outputLabel).put("user", userSerial)
            .put("signer", signer).put("output", key)
        prefs.edit().putString(rule.packageName, record.toString()).apply()
        if (armed?.packageName == app.packageName) disarm(true)
        saved[app.packageName] = rule
        changed()
        return true
    }

    fun remove(pkg: String) {
        saved.remove(pkg) ?: return
        prefs.edit().remove(pkg).apply()
        if (armed?.packageName == pkg) disarm(true) else changed()
    }

    fun arm(pkg: String): Boolean {
        val rule = saved[pkg] ?: return false
        val s = permissions()
        if (s.active == Backend.STANDARD || s.backends[s.active]?.status != BackendStatus.READY ||
            !rule.matches(pkg, userSerial, identity(pkg)?.second) || routing.state.active) return false
        if (armed != null) disarm(false)
        generation = s.generation
        backend = s.active
        armed = rule
        lastTracks = null
        lastOutputs = null
        val filter = IntentFilter().apply {
            addAction(Intent.ACTION_PACKAGE_REMOVED); addAction(Intent.ACTION_PACKAGE_REPLACED); addDataScheme("package")
        }
        try {
            if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(packages, filter, null, main, Context.RECEIVER_NOT_EXPORTED)
            else context.registerReceiver(packages, filter, null, main)
            audio.registerAudioPlaybackCallback(playback, main)
            lastTracks = audio.activePlaybackConfigurations.filter(::mediaCandidate).toSet()
        } catch (_: RuntimeException) { disarm(false); return false }
        if (backend == Backend.DIRECT_ROOT) main.postDelayed(expire, NativeAppRoutingPipe.LEASE_MS)
        changed()
        schedule()
        return true
    }

    fun disarm(restore: Boolean) {
        if (armed == null) return
        armed = null
        main.removeCallbacks(check)
        main.removeCallbacks(expire)
        runCatching { audio.unregisterAudioPlaybackCallback(playback) }
        runCatching { context.unregisterReceiver(packages) }
        lastTracks = null
        lastOutputs = null
        val owned = ownsRoute
        ownsRoute = false
        if (restore && owned && routing.state.active) routing.restore()
        changed()
    }

    fun backendChanged(s: PermissionSnapshot) {
        if (active && (s.generation != generation || s.active != backend || s.backends[backend]?.status != BackendStatus.READY)) disarm(true)
    }

    fun outputsChanged() {
        if (!active) return
        val outputs = routing.physicalOutputs()
        if (outputs != lastOutputs) { lastOutputs = outputs; schedule() }
    }

    fun routeChanged() {
        val rule = armed ?: return
        val s = routing.state
        if (!ownsRoute || s.packageName != rule.packageName || s.active) return
        ownsRoute = false
        if (s.reason !in setOf("already_on_output", "no_active_media", "playback_ended", "target_disconnected")) {
            disarm(false)
        }
    }

    private fun schedule() {
        if (!active) return
        main.removeCallbacks(check)
        main.postDelayed(check, 300)
    }

    private fun mediaCandidate(config: AudioPlaybackConfiguration): Boolean =
        config.audioAttributes.usage == AudioAttributes.USAGE_MEDIA || config.audioAttributes.usage == AudioAttributes.USAGE_UNKNOWN

    private fun evaluate() {
        val rule = armed ?: return
        val s = permissions()
        if (s.generation != generation || s.active != backend || s.backends[backend]?.status != BackendStatus.READY) { disarm(true); return }
        if (routing.state.active || lastTracks.isNullOrEmpty()) return
        val identity = identity(rule.packageName)
        if (identity == null || !rule.matches(rule.packageName, userSerial, identity.second)) { disarm(true); return }
        val output = rule.resolve(routing.physicalOutputs(), ::stableKey) ?: return
        ownsRoute = true
        routing.apply(AppCatalogEntry(rule.packageName, rule.appLabel, identity.first), output, s, rule.signer)
    }

    private fun identity(pkg: String): Pair<Int, String>? = runCatching {
        val pm = context.packageManager
        val uid = pm.getApplicationInfo(pkg, 0).uid
        require(uid != Process.myUid() && UserHandle.getUserHandleForUid(uid) == Process.myUserHandle() &&
            pm.getPackagesForUid(uid)?.singleOrNull() == pkg)
        uid to NativeAppRoutingIdentity.packageSigner(context, pkg)
    }.getOrNull()

    private fun stableKey(output: NativeAppOutput): String? = when (output.type) {
        AudioDeviceInfo.TYPE_BUILTIN_EARPIECE, AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> "builtin/${output.type}"
        AudioDeviceInfo.TYPE_BLUETOOTH_A2DP, AudioDeviceInfo.TYPE_BLE_HEADSET, AudioDeviceInfo.TYPE_BLE_SPEAKER, AudioDeviceInfo.TYPE_HEARING_AID -> {
            NativeSavedRoute.bondedOutput(output.type, output.address) { address ->
                if (Build.VERSION.SDK_INT >= 31 &&
                    context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return@bondedOutput false
                runCatching { context.getSystemService(BluetoothManager::class.java).adapter?.bondedDevices.orEmpty()
                    .any { it.address.equals(address, true) } }.getOrDefault(false)
            }
        }
        else -> null
    }
}
