package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.app.LocaleManager
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.view.View
import android.view.accessibility.AccessibilityNodeInfo
import android.widget.RadioGroup
import org.sonicroute.app.MainActivity
import org.sonicroute.app.R
import org.sonicroute.platform.AppAppearance
import org.sonicroute.platform.AppLanguage
import org.sonicroute.platform.AppTheme

/** Changes only this app's settings and restores the original theme and exact platform locale list. */
internal class AppearanceInterop(private val instrument: Instrumentation, private val evidence: String?) {
    private val pages = InteropActivity(instrument)
    private fun <T> ui(action: () -> T): T = instrument.onMain(action)
    private fun await(test: () -> Boolean) {
        val end = SystemClock.elapsedRealtime() + 12000
        do { if (test()) return; SystemClock.sleep(80) } while (SystemClock.elapsedRealtime() < end)
        error("appearance_timeout")
    }

    fun run() {
        val context = instrument.targetContext
        val originalTheme = AppAppearance.theme(context)
        val originalLanguage = AppAppearance.language(context)
        val oldLocales = if (Build.VERSION.SDK_INT >= 33) context.getSystemService(LocaleManager::class.java).applicationLocales else null
        val systemLocales = if (Build.VERSION.SDK_INT >= 33) context.getSystemService(LocaleManager::class.java).systemLocales
            else android.content.res.Resources.getSystem().configuration.locales
        val receipt = Bundle()
        val gallery = evidence?.let { PhoneUiEvidence(instrument, it) }
        var orientation: Int? = null
        var code = Activity.RESULT_CANCELED
        try {
            val first = pages.launch()
            orientation = ui { first.requestedOrientation }
            ui { first.findViewById<RadioGroup>(R.id.navigation).check(R.id.nav_settings) }
            fun choose(button: Int, option: String, ready: (MainActivity) -> Boolean) {
                ui { check(pages.current!!.findViewById<View>(button).performClick()) }
                await {
                    instrument.uiAutomation.rootInActiveWindow?.findAccessibilityNodeInfosByText(option)?.any { raw ->
                        var node: AccessibilityNodeInfo? = raw
                        while (node != null && !node.isClickable) node = node.parent
                        node?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true
                    } == true
                }
                await { ui { pages.current?.let { ready(it) && it.findViewById<RadioGroup>(R.id.navigation).checkedRadioButtonId == R.id.nav_settings } == true } }
            }
            for (theme in listOf(AppTheme.DARK, AppTheme.LIGHT)) {
                val mode = if (theme == AppTheme.DARK) Configuration.UI_MODE_NIGHT_YES else Configuration.UI_MODE_NIGHT_NO
                val label = ui { pages.current!!.getString(if (theme == AppTheme.DARK) R.string.appearance_dark else R.string.appearance_light) }
                choose(R.id.appearance_theme, label) { AppAppearance.theme(it) == theme && it.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == mode }
                if (theme == AppTheme.DARK && gallery != null) photographPages(gallery, "dark")
            }
            for ((lang, label) in listOf(AppLanguage.CHINESE to "简体中文", AppLanguage.ENGLISH to "English")) {
                choose(R.id.appearance_language, label) {
                    AppAppearance.language(it) == lang && it.getString(R.string.nav_settings) == (if (lang == AppLanguage.CHINESE) "设置" else "Settings") &&
                        AppAppearance.forStrings(context).getString(org.sonicroute.platform.R.string.connection_stop) ==
                            (if (lang == AppLanguage.CHINESE) "停止连接" else "Stop connection")
                }
                if (lang == AppLanguage.ENGLISH && gallery != null) photographPages(gallery, "light-en")
            }
            if (gallery != null) {
                ui { pages.current!!.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
                await {
                    ui { pages.current?.let { page ->
                        val decor = page.window.decorView
                        page.resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE && decor.width > decor.height
                    } == true }
                }
                // WindowManager composites the rotation after the resized activity has already reported idle.
                val animationScale = android.provider.Settings.Global.getFloat(context.contentResolver,
                    android.provider.Settings.Global.TRANSITION_ANIMATION_SCALE, 1f)
                SystemClock.sleep((850 * animationScale.coerceIn(0f, 10f)).toLong() + 200)
                photographPages(gallery, "landscape-en")
            }
            val after = if (Build.VERSION.SDK_INT >= 33) context.getSystemService(LocaleManager::class.java).systemLocales
                else android.content.res.Resources.getSystem().configuration.locales
            check(systemLocales == after)
            receipt.putString("appearance", "dark/light and zh-CN/en resource and tab readback passed")
            code = Activity.RESULT_OK
        } catch (e: Exception) { receipt.putString("failure", e.cause?.message ?: e.message ?: e.javaClass.simpleName) }
        finally {
            ui {
                orientation?.let { pages.current?.requestedOrientation = it }
                AppAppearance.setTheme(context, originalTheme)
                if (Build.VERSION.SDK_INT >= 33 && oldLocales != null) context.getSystemService(LocaleManager::class.java).applicationLocales = oldLocales
                else AppAppearance.setLanguage(context, originalLanguage)
            }
            pages.close()
            instrument.finish(code, receipt)
        }
    }

    private fun photographPages(gallery: PhoneUiEvidence, scene: String) {
        val destinations = listOf(R.id.nav_audio, R.id.nav_devices, R.id.nav_pc, R.id.nav_rules, R.id.nav_settings)
        for (destination in destinations) {
            ui { pages.current!!.findViewById<RadioGroup>(R.id.navigation).check(destination) }
            await {
                val root = instrument.uiAutomation.rootInActiveWindow
                root?.packageName == "org.sonicroute.app" && ui {
                    pages.current?.let { it.hasWindowFocus() &&
                        it.findViewById<RadioGroup>(R.id.navigation).checkedRadioButtonId == destination } == true
                }
            }
            val name = instrument.targetContext.resources.getResourceEntryName(destination)
            gallery.save("$scene-$name")
        }
    }
}
