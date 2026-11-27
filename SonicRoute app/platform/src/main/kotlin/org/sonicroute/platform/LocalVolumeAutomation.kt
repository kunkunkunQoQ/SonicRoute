package org.sonicroute.platform

import android.content.Context
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import org.sonicroute.core.AudioOutputKind
import org.sonicroute.core.LocalVolumeRule
import org.sonicroute.core.LocalVolumeRules
import org.sonicroute.core.OutputEvent
import org.sonicroute.core.RuleResult

internal class LocalVolumeAutomation(
    context: Context,
    private val applyVolume: (Int) -> RuleResult,
    private val onChanged: () -> Unit
) {
    private val preferences = context.getSharedPreferences("output_volume_rules", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private val rules = LocalVolumeRules(OutputEvent.entries.mapNotNull { event ->
        preferences.getString(event.name, null)?.let { json ->
            runCatching {
                val obj = JSONObject(json)
                LocalVolumeRule(event, obj.getInt("percent"), obj.getBoolean("enabled"),
                    obj.optString("result").takeIf { it.isNotEmpty() }?.let(RuleResult::valueOf),
                    obj.optLong("time"))
            }.getOrNull()
        }
    })
    var snapshot = rules.snapshot()
        private set
    val active: Boolean get() = snapshot.any { it.enabled }
    private var latest: Set<AudioOutputKind>? = null
    private val processChange = Runnable {
        val outputs = latest ?: return@Runnable
        if (rules.onOutputs(outputs, System.currentTimeMillis(), ::execute)) persist()
    }

    fun onOutputs(kinds: Set<AudioOutputKind>) {
        if (!active || latest == kinds) return
        val first = latest == null
        latest = kinds
        main.removeCallbacks(processChange)
        if (first) processChange.run() else main.postDelayed(processChange, 450)
    }

    fun save(rule: LocalVolumeRule) {
        rules.set(rule)
        resetPending()
        persist()
    }

    fun remove(event: OutputEvent) {
        rules.remove(event)
        resetPending()
        persist()
    }

    private fun execute(percent: Int): RuleResult = try {
        applyVolume(percent)
    } catch (_: SecurityException) {
        RuleResult.PERMISSION
    } catch (_: RuntimeException) {
        RuleResult.REJECTED
    }

    private fun resetPending() {
        main.removeCallbacks(processChange)
        rules.resetBaseline()
        latest?.let { rules.onOutputs(it, 0, ::execute) }
    }

    private fun persist() {
        val old = snapshot.associateBy { it.event }
        snapshot = rules.snapshot()
        val editor = preferences.edit()
        for (event in old.keys - snapshot.map { it.event }.toSet()) editor.remove(event.name)
        for (rule in snapshot) {
            if (old[rule.event] == rule) continue
            val json = JSONObject().put("percent", rule.percent).put("enabled", rule.enabled)
                .put("time", rule.lastRunMillis).put("result", rule.lastResult?.name)
            editor.putString(rule.event.name, json.toString())
        }
        editor.apply()
        if (!active) {
            latest = null
            rules.resetBaseline()
            main.removeCallbacks(processChange)
        }
        onChanged()
    }
}
