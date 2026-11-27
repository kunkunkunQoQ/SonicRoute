package org.sonicroute.app

import android.app.Activity
import android.app.AlertDialog
import android.os.Build
import android.widget.Button
import org.sonicroute.platform.AppAppearance
import org.sonicroute.platform.AppLanguage
import org.sonicroute.platform.AppTheme

internal class AppearancePage(private val activity: Activity, private val languageChanged: () -> Unit) {
    private val theme = activity.findViewById<Button>(R.id.appearance_theme)
    private val language = activity.findViewById<Button>(R.id.appearance_language)
    private val languages get() = arrayOf(activity.getString(R.string.appearance_system), activity.getString(R.string.appearance_chinese),
        activity.getString(R.string.appearance_english))
    private val themes get() = arrayOf(activity.getString(R.string.appearance_system), activity.getString(R.string.appearance_light),
        activity.getString(R.string.appearance_dark))
    init {
        theme.setOnClickListener {
            val current = AppAppearance.theme(activity)
            choose(R.string.appearance_theme, themes, current.ordinal) { index ->
                AppAppearance.setTheme(activity, AppTheme.entries[index])
                activity.recreate()
            }
        }
        language.setOnClickListener {
            val current = AppAppearance.language(activity)
            choose(R.string.appearance_language, languages, current.ordinal) { index ->
                AppAppearance.setLanguage(activity, AppLanguage.entries[index])
                languageChanged()
                if (Build.VERSION.SDK_INT < 33) activity.recreate()
                else render()
            }
        }
        render()
    }
    fun render() {
        theme.text = activity.getString(R.string.appearance_value, activity.getString(R.string.appearance_theme), themes[AppAppearance.theme(activity).ordinal])
        language.text = activity.getString(R.string.appearance_value, activity.getString(R.string.appearance_language), languages[AppAppearance.language(activity).ordinal])
    }
    private fun choose(title: Int, values: Array<String>, current: Int, selected: (Int) -> Unit) {
        AlertDialog.Builder(activity).setTitle(title).setSingleChoiceItems(values, current) { dialog, index ->
            dialog.dismiss()
            if (index != current) selected(index)
        }.setNegativeButton(android.R.string.cancel, null).show()
    }
}
