package org.sonicroute.app

import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.media.AudioDeviceInfo
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.Editable
import android.text.TextWatcher
import android.view.View
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.ArrayAdapter
import android.widget.AdapterView
import android.widget.Button
import android.widget.EditText
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import org.sonicroute.core.AppCatalogEntry
import org.sonicroute.core.AppCatalogSnapshot
import org.sonicroute.core.AudioOutputSink
import org.sonicroute.core.Backend
import org.sonicroute.core.BackendStatus
import org.sonicroute.platform.NativeAppOutput
import org.sonicroute.platform.LauncherCatalogState
import org.sonicroute.core.NativeSavedRoute
import org.sonicroute.platform.SonicRouteRuntime
import org.sonicroute.platform.SonicRouteRuntimeSnapshot

internal class NativeAppRouteDialog(private val activity: Activity, private val runtime: SonicRouteRuntime,
    private val openPermissions: () -> Unit) {
    private var dialog: AlertDialog? = null
    private lateinit var apps: Spinner
    private lateinit var search: EditText
    private lateinit var clearSearch: View
    private lateinit var emptySearch: TextView
    private lateinit var outputs: Spinner
    private lateinit var status: TextView
    private lateinit var permissionsAction: Button
    private lateinit var rules: Spinner
    private lateinit var ruleControls: View
    private lateinit var useRule: Button
    private lateinit var saveRule: Button
    private var candidates = emptyList<AppCatalogEntry>()
    private var renderedCatalog: AppCatalogSnapshot? = null
    private var preferredPackage: String? = null
    private var preferredUid: Int? = null
    private var devices = emptyList<NativeAppOutput>()
    private var observedOutputs: List<AudioOutputSink>? = null
    private var selectedOutputRemoved = false
    private var savedRules = emptyList<NativeSavedRoute>()

    fun show(saved: Bundle? = null) {
        if (dialog != null) return
        val snapshot = runtime.currentSnapshot()
        candidates = emptyList()
        observedOutputs = snapshot.audio.outputs
        devices = runtime.nativeAppOutputs()
        preferredPackage = saved?.getString("app") ?: snapshot.nativeAppRoute.packageName
        preferredUid = saved?.getInt("uid", -1)?.takeIf { it >= 0 }
        val rememberedOutput = saved?.getInt("output", 0) ?: 0
        val restoredOutput = devices.firstOrNull { it.id == rememberedOutput && it.type == saved?.getInt("type") && it.address == saved?.getString("address") }
        selectedOutputRemoved = saved?.getBoolean("outputRemoved") == true || rememberedOutput != 0 && restoredOutput == null
        val view = activity.layoutInflater.inflate(R.layout.native_app_route_dialog, null)
        apps = view.findViewById(R.id.route_app)
        search = view.findViewById(R.id.route_app_search)
        clearSearch = view.findViewById(R.id.route_search_clear)
        emptySearch = view.findViewById(R.id.route_search_empty)
        outputs = view.findViewById(R.id.route_output)
        status = view.findViewById(R.id.route_result)
        permissionsAction = view.findViewById<Button>(R.id.route_permissions).apply {
            setOnClickListener { dismiss(); openPermissions() }
        }
        rules = view.findViewById(R.id.route_rules_list)
        ruleControls = view.findViewById(R.id.route_rule_controls)
        useRule = view.findViewById(R.id.route_rule_use)
        savedRules = emptyList()
        search.setText(saved?.getString("query").orEmpty())
        clearSearch.setOnClickListener { search.text.clear() }
        search.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
            override fun afterTextChanged(s: Editable?) {
                updateAppChoices()
                render(runtime.currentSnapshot())
            }
        })
        search.setOnEditorActionListener { _, action, _ ->
            if (action != EditorInfo.IME_ACTION_SEARCH) return@setOnEditorActionListener false
            activity.getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(search.windowToken, 0)
            if (candidates.size == 1) apps.setSelection(1)
            else if (candidates.isNotEmpty()) apps.performClick()
            true
        }
        apps.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                selectedApp()?.let { preferredPackage = it.packageName; preferredUid = it.uid }
                render(runtime.currentSnapshot())
            }
        }
        val existingRoute = snapshot.nativeAppRoute
        setOutputChoices(if (saved != null) restoredOutput else
            devices.firstOrNull { existingRoute.active && it.id == existingRoute.targetId })
        outputs.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                if (position > 0) selectedOutputRemoved = false
                render(runtime.currentSnapshot())
            }
        }
        saveRule = view.findViewById<Button>(R.id.route_rule_save).apply {
            isEnabled = candidates.isNotEmpty() && devices.isNotEmpty()
            setOnClickListener {
                val app = selectedApp() ?: return@setOnClickListener
                val target = currentOutputForSubmission() ?: return@setOnClickListener
                if (!runtime.saveNativeRouteRule(app.packageName, app.uid, target, deviceName(target))) {
                    Toast.makeText(activity, R.string.native_rule_unavailable, Toast.LENGTH_LONG).show()
                } else rules.setSelection(savedRules.indexOfFirst { it.packageName == app.packageName }.coerceAtLeast(0))
            }
        }
        useRule.setOnClickListener {
            val selected = savedRules.getOrNull(rules.selectedItemPosition) ?: return@setOnClickListener
            if (!runtime.useNativeRouteRule(selected.packageName)) {
                Toast.makeText(activity, R.string.native_route_platform, Toast.LENGTH_SHORT).show()
            }
        }
        rules.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onNothingSelected(parent: AdapterView<*>?) = Unit
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                render(runtime.currentSnapshot())
            }
        }
        view.findViewById<Button>(R.id.route_rule_delete).setOnClickListener {
            savedRules.getOrNull(rules.selectedItemPosition)?.let { runtime.removeNativeRouteRule(it.packageName) }
        }
        if (listOf(Build.MANUFACTURER, Build.BRAND).any { it.lowercase() in setOf("xiaomi", "redmi", "poco") }) {
            view.findViewById<Button>(R.id.route_vendor_volume).apply {
                visibility = View.VISIBLE
                setOnClickListener {
                    try { activity.startActivity(Intent(Settings.ACTION_SOUND_SETTINGS)) }
                    catch (_: ActivityNotFoundException) { Toast.makeText(activity, R.string.settings_unavailable, Toast.LENGTH_SHORT).show() }
                    catch (_: SecurityException) { Toast.makeText(activity, R.string.settings_unavailable, Toast.LENGTH_SHORT).show() }
                }
            }
        }
        val window = AlertDialog.Builder(activity).setTitle(R.string.native_route_title).setView(view)
            .setPositiveButton(R.string.native_route_apply, null)
            .setNeutralButton(R.string.native_route_restore, null)
            .setNegativeButton(android.R.string.cancel, null).create()
        dialog = window
        window.setOnDismissListener {
            dialog = null
            candidates = emptyList()
            devices = emptyList()
            observedOutputs = null
            renderedCatalog = null
            preferredPackage = null
            preferredUid = null
            runtime.setAppPickerOpen(false)
        }
        window.show()
        window.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val app = selectedApp() ?: return@setOnClickListener
            val device = currentOutputForSubmission() ?: return@setOnClickListener
            if (!runtime.routeLocalApp(app.packageName, app.uid, device)) status.setText(R.string.native_route_ident)
        }
        window.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener { runtime.restoreLocalAppRoute() }
        runtime.setAppPickerOpen(true)
        render(runtime.currentSnapshot())
    }

    fun dismiss() { dialog?.dismiss() }

    fun saveState(): Bundle? {
        if (dialog?.isShowing != true) return null
        return Bundle().apply {
            putString("query", search.text.toString())
            putString("app", preferredPackage)
            putInt("uid", preferredUid ?: -1)
            putBoolean("outputRemoved", selectedOutputRemoved)
            selectedOutput()?.let { putInt("output", it.id); putInt("type", it.type); putString("address", it.address) }
        }
    }

    fun render(snapshot: SonicRouteRuntimeSnapshot) {
        val window = dialog?.takeIf { it.isShowing } ?: return
        val route = snapshot.nativeAppRoute
        val catalog = snapshot.audio
        if (observedOutputs !== catalog.outputs) {
            observedOutputs = catalog.outputs
            refreshDeviceChoices()
        }
        if (renderedCatalog !== catalog.appCatalog) {
            renderedCatalog = catalog.appCatalog
            if (preferredUid != null && catalog.catalogState == LauncherCatalogState.READY &&
                catalog.appCatalog.entries.none { !it.isSelf && it.packageName == preferredPackage && it.uid == preferredUid }) {
                dismiss()
                Toast.makeText(activity, R.string.native_route_ident, Toast.LENGTH_SHORT).show()
                return
            }
            updateAppChoices()
        }
        val noMatch = catalog.catalogState == LauncherCatalogState.READY && search.text.isNotBlank() && candidates.isEmpty()
        emptySearch.visibility = if (noMatch) View.VISIBLE else View.GONE
        apps.visibility = if (noMatch) View.GONE else View.VISIBLE
        val choices = selectedApp() != null && selectedOutput() != null
        saveRule.isEnabled = choices
        val permissions = snapshot.permissions
        val ready = permissions.active in setOf(Backend.SHIZUKU_SHELL, Backend.SHIZUKU_ROOT, Backend.DIRECT_ROOT) &&
            permissions.backends[permissions.active]?.status == BackendStatus.READY
        permissionsAction.visibility = if (!ready && !route.active) View.VISIBLE else View.GONE
        val preference = snapshot.nativeRouteRules
        if (savedRules != preference.saved) {
            val previous = savedRules.getOrNull(rules.selectedItemPosition)?.packageName
            savedRules = preference.saved
            setChoices(rules, savedRules.map { "${it.appLabel} (${it.packageName}) → ${it.outputLabel}" },
                savedRules.indexOfFirst { it.packageName == (preference.armed ?: previous) })
        }
        ruleControls.visibility = if (savedRules.isEmpty()) View.GONE else View.VISIBLE
        val chosen = savedRules.getOrNull(rules.selectedItemPosition)?.packageName
        val watching = chosen != null && chosen == preference.armed
        useRule.setText(if (watching) R.string.native_rule_pause else R.string.native_rule_use)
        useRule.isEnabled = watching || ready && !route.active && chosen != null
        apps.isEnabled = !route.active
        search.isEnabled = !route.active
        clearSearch.isEnabled = !route.active
        outputs.isEnabled = !route.active
        window.getButton(AlertDialog.BUTTON_POSITIVE).isEnabled = !route.active && ready && choices
        window.getButton(AlertDialog.BUTTON_NEUTRAL).isEnabled = route.active && route.phase != "restoring"
        val message = when {
            route.phase == "applying" -> R.string.native_route_checking
            route.phase == "restoring" -> R.string.native_route_restoring
            selectedOutputRemoved -> R.string.route_choose_replacement
            route.phase == "verified" -> R.string.native_route_verified
            route.phase == "restored" -> R.string.native_route_restored
            preference.armed != null && !route.active -> R.string.native_rule_waiting
            route.reason == "already_on_output" -> R.string.native_route_already
            route.reason == "no_active_media" -> R.string.native_route_playback_missing
            route.reason in setOf("ambiguous_app_identity", "app_removed") -> R.string.native_route_ident
            route.reason in setOf("unverified_caller", "direct_peer_identity_unverified", "backend_connection_timed_out") -> R.string.native_route_root_identity
            route.reason == "target_disconnected" -> R.string.native_route_no_target
            route.reason == "policy_release_failed" -> R.string.native_route_release_failed
            route.reason == "direct_lease_expired" -> R.string.native_route_root_expired
            route.reason in setOf("device_readback_unavailable", "target_readback_failed") -> R.string.native_route_no_readback
            route.reason in setOf("policy_released_readback_changed", "release_readback_unavailable") -> R.string.native_route_changed
            catalog.catalogState == LauncherCatalogState.FAILED -> R.string.app_catalog_failed
            catalog.catalogState != LauncherCatalogState.READY -> R.string.app_catalog_loading
            !ready || route.reason == "routing_permission_required" -> R.string.native_route_needs_backend
            route.phase == "error" -> R.string.native_route_platform
            route.phase == "released" -> R.string.native_route_released
            candidates.isEmpty() && !noMatch || devices.isEmpty() -> R.string.native_route_empty
            selectedApp() == null -> R.string.route_select_app
            selectedOutput() == null -> R.string.route_select_output
            else -> R.string.native_route_ready
        }
        status.text = buildString {
            append(activity.getString(message))
            if (route.phase == "verified" && permissions.active == Backend.DIRECT_ROOT) {
                append('\n'); append(activity.getString(R.string.native_route_root_lease))
            }
            if (route.originalDeviceIds.isNotEmpty()) {
                append('\n')
                append(activity.getString(R.string.native_route_observed, route.originalDeviceIds.joinToString(),
                    route.actualDeviceIds.joinToString().ifEmpty { "—" }))
            }
        }
    }

    private fun selectedApp() = candidates.getOrNull(apps.selectedItemPosition - 1)

    private fun updateAppChoices() {
        val query = search.text.toString().trim()
        clearSearch.visibility = if (search.text.isNotEmpty()) View.VISIBLE else View.GONE
        candidates = renderedCatalog?.entries.orEmpty().filter { !it.isSelf &&
            (query.isEmpty() || it.label.contains(query, ignoreCase = true) || it.packageName.contains(query, ignoreCase = true)) }
        val index = candidates.indexOfFirst { it.packageName == preferredPackage && (preferredUid == null || it.uid == preferredUid) }
        setChoices(apps, listOf(activity.getString(R.string.route_select_app)) + candidates.map { "${it.label}\n${it.packageName}" }, index + 1)
    }

    private fun selectedOutput() = devices.getOrNull(outputs.selectedItemPosition - 1)

    private fun setOutputChoices(selected: NativeAppOutput?) {
        val names = devices.map(::deviceName)
        val counts = names.groupingBy { it }.eachCount()
        val labels = devices.mapIndexed { index, device ->
            val name = names[index]
            if (counts.getValue(name) == 1) name else activity.getString(R.string.native_route_target, name, device.id)
        }
        setChoices(outputs, listOf(activity.getString(R.string.route_select_output)) + labels, devices.indexOf(selected) + 1)
    }

    private fun refreshDeviceChoices() {
        val current = runtime.nativeAppOutputs()
        if (devices == current) return
        val prior = selectedOutput()
        val preserved = prior?.let { old -> current.firstOrNull { it.id == old.id && it.type == old.type && it.address == old.address } }
        if (prior != null && preserved == null) selectedOutputRemoved = true
        devices = current
        setOutputChoices(preserved)
    }

    private fun currentOutputForSubmission(): NativeAppOutput? {
        val chosen = selectedOutput() ?: return null
        refreshDeviceChoices()
        render(runtime.currentSnapshot())
        return selectedOutput()?.takeIf { it.id == chosen.id && it.type == chosen.type && it.address == chosen.address }
    }

    private fun setChoices(spinner: Spinner, items: List<String>, selected: Int) {
        val appList = spinner === apps
        spinner.adapter = ArrayAdapter(activity, if (appList) R.layout.route_app_option else android.R.layout.simple_spinner_item, items).apply {
            setDropDownViewResource(if (appList) R.layout.route_app_option else android.R.layout.simple_spinner_dropdown_item)
        }
        if (selected >= 0) spinner.setSelection(selected)
    }

    private fun deviceName(device: NativeAppOutput): String =
        when (device.type) {
            AudioDeviceInfo.TYPE_BUILTIN_EARPIECE -> activity.getString(R.string.native_route_earpiece)
            AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> activity.getString(R.string.native_route_speaker)
            AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_WIRED_HEADPHONES -> activity.getString(R.string.native_route_wired)
            else -> device.name.ifBlank { activity.getString(R.string.native_route_external) }
        }
}
