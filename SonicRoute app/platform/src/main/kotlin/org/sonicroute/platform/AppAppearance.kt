package org.sonicroute.platform

import android.app.LocaleManager
import android.content.Context
import android.content.res.Configuration
import android.os.Build
import android.os.LocaleList
import java.util.Locale

enum class AppTheme { SYSTEM, LIGHT, DARK }
enum class AppLanguage(val tag: String) { SYSTEM(""), CHINESE("zh-CN"), ENGLISH("en") }

object AppAppearance {
    private fun prefs(context: Context) = context.getSharedPreferences("app_appearance", Context.MODE_PRIVATE)
    fun theme(context: Context): AppTheme {
        val saved = prefs(context).getString("theme", null)
        return AppTheme.entries.find { it.name == saved } ?: AppTheme.SYSTEM
    }
    fun setTheme(context: Context, value: AppTheme) { prefs(context).edit().putString("theme", value.name).apply() }
    fun language(context: Context): AppLanguage {
        val tag = if (Build.VERSION.SDK_INT >= 33) context.getSystemService(LocaleManager::class.java).applicationLocales.toLanguageTags()
            else prefs(context).getString("locale", "").orEmpty()
        val primary = Locale.forLanguageTag(tag.substringBefore(',')).language
        return when (primary) { "en" -> AppLanguage.ENGLISH; "zh" -> AppLanguage.CHINESE; else -> AppLanguage.SYSTEM }
    }
    fun setLanguage(context: Context, value: AppLanguage) {
        if (Build.VERSION.SDK_INT >= 33) context.getSystemService(LocaleManager::class.java).applicationLocales = LocaleList.forLanguageTags(value.tag)
        else prefs(context).edit().putString("locale", value.tag).apply()
    }

    /** After an OS upgrade, transfer the old app-only preference once. System Settings then owns the choice. */
    fun migrateLanguage(context: Context) {
        if (Build.VERSION.SDK_INT < 33) return
        val settings = prefs(context)
        val tag = settings.getString("locale", null) ?: return
        val manager = context.getSystemService(LocaleManager::class.java)
        if (manager.applicationLocales.isEmpty && AppLanguage.entries.any { it.tag == tag && tag.isNotEmpty() })
            manager.applicationLocales = LocaleList.forLanguageTags(tag)
        settings.edit().remove("locale").apply()
    }

    fun activityOverride(base: Context): Configuration? {
        val theme = theme(base)
        val locale = if (Build.VERSION.SDK_INT < 33) language(base) else AppLanguage.SYSTEM
        if (theme == AppTheme.SYSTEM && locale == AppLanguage.SYSTEM) return null
        val override = Configuration()
        if (theme != AppTheme.SYSTEM) {
            override.uiMode = (base.resources.configuration.uiMode and Configuration.UI_MODE_TYPE_MASK) or
                if (theme == AppTheme.DARK) Configuration.UI_MODE_NIGHT_YES else Configuration.UI_MODE_NIGHT_NO
        }
        if (locale != AppLanguage.SYSTEM) override.setLocales(LocaleList.forLanguageTags(locale.tag))
        return override
    }

    /** Used by foreground services and runtime resources before API 33, and while a framework configuration changes. */
    fun forStrings(base: Context): Context {
        val locales = if (Build.VERSION.SDK_INT >= 33) {
            val manager = base.getSystemService(LocaleManager::class.java)
            manager.applicationLocales.takeUnless { it.isEmpty } ?: manager.systemLocales
        } else {
            val selected = language(base)
            if (selected == AppLanguage.SYSTEM) return base
            LocaleList.forLanguageTags(selected.tag)
        }
        if (locales.isEmpty || base.resources.configuration.locales[0] == locales[0]) return base
        return base.createConfigurationContext(Configuration().apply { setLocales(locales) })
    }
}
