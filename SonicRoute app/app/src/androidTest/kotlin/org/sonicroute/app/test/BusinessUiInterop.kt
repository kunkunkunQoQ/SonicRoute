package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import android.os.SystemClock
import android.view.KeyEvent
import android.view.View
import android.view.accessibility.AccessibilityNodeInfo
import android.widget.RadioGroup
import org.sonicroute.app.R
import org.sonicroute.core.OutputEvent
import org.sonicroute.platform.SonicRouteRuntime

/** Drives the visible application picker and one new device rule. All pre-existing rules are preserved. */
internal class BusinessUiInterop(private val instrument: Instrumentation, private val evidence: String?) {
    private val pages = InteropActivity(instrument)
    private val product = "org.sonicroute.app"
    private val root get() = instrument.uiAutomation.rootInActiveWindow?.takeIf { it.packageName == product }
    private fun find(id: String) = root?.findAccessibilityNodeInfosByViewId(id)?.firstOrNull()
    private fun appId(name: String) = "$product:id/$name"

    private fun await(description: String, condition: () -> Boolean) {
        val deadline = SystemClock.elapsedRealtime() + 9000
        do { if (condition()) return; SystemClock.sleep(90) } while (SystemClock.elapsedRealtime() < deadline)
        error(description)
    }
    private fun tap(node: AccessibilityNodeInfo?) {
        var target = requireNotNull(node)
        while (!target.isClickable && target.parent != null) target = target.parent
        check(target.isEnabled && target.performAction(AccessibilityNodeInfo.ACTION_CLICK))
        instrument.waitForIdleSync()
    }
    private fun textLabel(text: String) = root?.findAccessibilityNodeInfosByText(text)?.firstOrNull { it.text?.toString() == text }

    fun run() {
        val receipt = Bundle()
        var code = Activity.RESULT_CANCELED
        var stage = "launch"
        var restore: (() -> Unit)? = null
        try {
            val page = pages.launch()
            val gallery = evidence?.let { PhoneUiEvidence(instrument, it) }
            val runtime = instrument.onMain { SonicRouteRuntime.getInstance(instrument.targetContext) }
            fun nav(id: Int) = instrument.onMain { page.findViewById<RadioGroup>(R.id.navigation).check(id) }
            fun press(id: Int) = instrument.onMain { page.findViewById<View>(id).let { check(it.isShown && it.isEnabled && it.performClick()) } }
            fun query(value: String) {
                val input = requireNotNull(find(appId("route_app_search")))
                check(input.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply {
                    putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value)
                }))
                await("query_not_committed") { find(appId("route_app_search"))?.text?.toString() == value }
            }
            stage = "application-picker"
            nav(R.id.nav_audio)
            press(R.id.native_app_route)
            await("route_dialog_missing") { find(appId("route_app_search")) != null }
            await("catalog_unavailable") { instrument.onMain { runtime.currentSnapshot().audio.appCatalog.entries.any { it.packageName == "bin.mt.plus" } } }
            check(textLabel(page.getString(R.string.route_select_output)) != null) { "unexpected_preselected_output" }
            gallery?.save("route-dialog")
            stage = "search-real-package"
            query("bin.mt.plus")
            tap(find(appId("route_app")))
            await("known_package_missing") {
                root?.findAccessibilityNodeInfosByText("bin.mt.plus")?.any { it.viewIdResourceName != appId("route_app_search") && it.text?.contains("bin.mt.plus") == true } == true
            }
            gallery?.save("route-search-mt")
            instrument.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
            await("picker_did_not_close") { find(appId("route_app_search")) != null }
            stage = "empty-search"
            query("zz.sonicroute.no.such.installed.package")
            await("empty_state_missing") { find(appId("route_search_empty")) != null }
            gallery?.save("route-search-empty")
            tap(find(appId("route_search_clear")))
            await("clear_did_not_restore_catalog") { find(appId("route_search_empty")) == null && find(appId("route_search_clear")) == null }
            stage = "physical-output-picker"
            tap(find(appId("route_output")))
            val speaker = page.getString(R.string.native_route_speaker)
            await("speaker_not_offered") { root?.findAccessibilityNodeInfosByText(speaker)?.any { it.isVisibleToUser } == true }
            gallery?.save("route-output-options")
            instrument.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
            await("output_picker_did_not_close") { find(appId("route_app_search")) != null }
            val accessEntry = find(appId("route_permissions"))
            if (accessEntry != null && accessEntry.isVisibleToUser) {
                tap(accessEntry)
                await("permission_section_did_not_open") {
                    instrument.onMain { page.findViewById<RadioGroup>(R.id.navigation).checkedRadioButtonId == R.id.nav_settings &&
                        page.findViewById<View>(R.id.backend_list).isShown }
                }
                gallery?.save("route-permission-entry")
                receipt.putString("route_permissions", "opened expanded Settings access methods")
            } else {
                instrument.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
                await("route_dialog_did_not_close") { find(appId("native_app_route")) != null }
            }
            stage = "new-rule"
            nav(R.id.nav_rules)
            val original = instrument.onMain { runtime.currentSnapshot().volumeRules.toList() }
            val unused = OutputEvent.entries.firstOrNull { event -> original.none { it.event == event } }
                ?: error("all_rule_slots_in_use")
            val percent = instrument.onMain { runtime.currentSnapshot().audio.volume.percent }
            press(R.id.add_rule)
            await("rule_editor_missing") { find("android:id/button1") != null }
            gallery?.save("rule-editor")
            // The editor defaults to the first unused event and the current volume.
            restore = { instrument.onMain {
                if (runtime.currentSnapshot().volumeRules.any { it.event == unused && it.percent == percent })
                    runtime.removeVolumeRule(unused)
            } }
            tap(find("android:id/button1"))
            fun rule() = instrument.onMain { runtime.currentSnapshot().volumeRules.firstOrNull { it.event == unused } }
            await("new_rule_not_saved") { rule()?.let { it.percent == percent && it.enabled } == true }
            gallery?.save("rule-saved")
            fun ruleLabel() = instrument.onMain { page.findViewById<android.widget.LinearLayout>(R.id.rule_list).let { rows ->
                val eventLabel = page.getString(when (unused) {
                    OutputEvent.BLUETOOTH_CONNECTED -> R.string.rule_bluetooth_on
                    OutputEvent.BLUETOOTH_DISCONNECTED -> R.string.rule_bluetooth_off
                    OutputEvent.WIRED_CONNECTED -> R.string.rule_wired_on
                    OutputEvent.WIRED_DISCONNECTED -> R.string.rule_wired_off
                    OutputEvent.USB_CONNECTED -> R.string.rule_usb_on
                    OutputEvent.USB_DISCONNECTED -> R.string.rule_usb_off
                })
                (0 until rows.childCount).mapNotNull { (rows.getChildAt(it) as? android.widget.Button)?.text?.toString() }.single { it.contains(eventLabel) }
            } }
            stage = "open-saved-rule"
            await("saved_rule_not_visible") { textLabel(ruleLabel()) != null }
            tap(textLabel(ruleLabel()))
            await("rule_menu_missing") { textLabel(page.getString(R.string.rule_disable)) != null }
            gallery?.save("rule-menu")
            stage = "pause-rule"
            tap(textLabel(page.getString(R.string.rule_disable)))
            await("pause_not_saved") { rule()?.enabled == false }
            stage = "open-paused-rule"
            await("paused_rule_not_visible") { textLabel(ruleLabel()) != null }
            tap(textLabel(ruleLabel()))
            stage = "delete-rule"
            await("delete_action_missing") { textLabel(page.getString(R.string.rule_remove)) != null }
            tap(textLabel(page.getString(R.string.rule_remove)))
            await("delete_confirmation_missing") { find("android:id/button1") != null }
            tap(find("android:id/button1"))
            await("rule_not_deleted") { rule() == null }
            check(instrument.onMain { runtime.currentSnapshot().volumeRules == original })
            restore = null
            receipt.putString("business_ui", "real installed package search, empty/clear, explicit physical output choice, rule create/pause/delete passed; original rules restored")
            code = Activity.RESULT_OK
        } catch (e: Exception) {
            val problem = e.cause ?: e
            val line = problem.stackTrace.firstOrNull { it.className.contains("BusinessUiInterop") }?.lineNumber
            receipt.putString("failure", "$stage ${problem.message ?: problem.javaClass.simpleName} line=$line")
        }
        finally {
            try { restore?.invoke() } finally { pages.close(); instrument.finish(code, receipt) }
        }
    }
}
