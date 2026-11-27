package org.sonicroute.app

import android.app.Activity
import android.app.AlertDialog
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Spinner
import android.widget.TextView
import org.sonicroute.core.LocalVolumeRule
import org.sonicroute.core.OutputEvent
import org.sonicroute.core.RuleResult
import org.sonicroute.platform.SonicRouteRuntime
import java.text.DateFormat
import java.util.Date

internal class VolumeRulesPage(private val activity: Activity, private val runtime: SonicRouteRuntime) {
    private val container = activity.findViewById<LinearLayout>(R.id.rule_list)
    private val add = activity.findViewById<Button>(R.id.add_rule)
    private val empty = activity.findViewById<TextView>(R.id.rules_empty)
    private val scope = activity.findViewById<TextView>(R.id.rules_scope)
    private var last: List<LocalVolumeRule>? = null
    init { add.setOnClickListener { edit(null) } }

    fun render(rules: List<LocalVolumeRule>) {
        if (last == rules) return
        last = rules
        container.removeAllViews()
        empty.visibility = if (rules.isEmpty()) View.VISIBLE else View.GONE
        scope.visibility = if (rules.isEmpty()) View.GONE else View.VISIBLE
        add.isEnabled = rules.size < OutputEvent.entries.size
        for (rule in rules) {
            val row = activity.rowButton().apply {
                text = activity.getString(R.string.rule_summary, eventName(rule.event), rule.percent,
                    activity.getString(if (rule.enabled) R.string.rule_enabled else R.string.rule_paused))
                setCompoundDrawablesRelativeWithIntrinsicBounds(R.drawable.ic_rules, 0, R.drawable.ic_more, 0)
                setOnClickListener { showRuleMenu(this, rule) }
            }
            container.addView(row)
            rule.lastResult?.let { result ->
                container.addView(TextView(activity).apply {
                    setTextAppearance(R.style.SonicRoute_Body)
                    text = activity.getString(R.string.rule_history, DateFormat.getDateTimeInstance(
                        DateFormat.SHORT, DateFormat.SHORT, activity.resources.configuration.locales[0]).format(Date(rule.lastRunMillis)), activity.getString(resultLabel(result)))
                })
            }
        }
    }

    private fun showRuleMenu(anchor: View, rule: LocalVolumeRule) {
        val toggle = if (rule.enabled) R.string.rule_disable else R.string.rule_enable
        PopupMenu(activity, anchor).apply {
            for (label in listOf(R.string.rule_edit, toggle, R.string.rule_remove)) menu.add(0, label, 0, label)
            setOnMenuItemClickListener { option ->
                when (option.itemId) {
                    R.string.rule_edit -> edit(rule)
                    toggle -> runtime.saveVolumeRule(rule.copy(enabled = !rule.enabled))
                    R.string.rule_remove -> AlertDialog.Builder(activity)
                        .setMessage(activity.getString(R.string.rule_delete_confirm, eventName(rule.event)))
                        .setNegativeButton(android.R.string.cancel, null)
                        .setPositiveButton(R.string.rule_remove) { _, _ -> runtime.removeVolumeRule(rule.event) }.show()
                }
                true
            }
            show()
        }
    }

    private fun edit(existing: LocalVolumeRule?) {
        val events = if (existing != null) listOf(existing.event)
            else OutputEvent.entries - runtime.currentSnapshot().volumeRules.map { it.event }.toSet()
        if (events.isEmpty()) return
        val body = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            val pad = resources.getDimensionPixelSize(R.dimen.screen_padding)
            setPadding(pad, pad / 2, pad, 0)
        }
        val trigger = Spinner(activity).apply {
            id = View.generateViewId()
            adapter = ArrayAdapter(activity, android.R.layout.simple_spinner_dropdown_item, events.map(::eventName))
            isEnabled = existing == null
        }
        body.addView(TextView(activity).apply { setText(R.string.rule_event); labelFor = trigger.id })
        body.addView(trigger)
        val label = TextView(activity)
        val gain = SeekBar(activity).apply {
            id = View.generateViewId()
            label.labelFor = id
            max = 100
            progress = existing?.percent ?: runtime.currentSnapshot().audio.volume.percent
            label.text = activity.getString(R.string.rule_level, progress)
            minimumHeight = resources.getDimensionPixelSize(R.dimen.touch_min_height)
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(bar: SeekBar, value: Int, fromUser: Boolean) {
                    label.text = activity.getString(R.string.rule_level, value)
                }
                override fun onStartTrackingTouch(bar: SeekBar) = Unit
                override fun onStopTrackingTouch(bar: SeekBar) = Unit
            })
        }
        body.addView(label)
        body.addView(gain)
        AlertDialog.Builder(activity).setTitle(if (existing == null) R.string.rule_add else R.string.rule_edit)
            .setView(ScrollView(activity).apply { addView(body) }).setNegativeButton(android.R.string.cancel, null)
            .setPositiveButton(R.string.rule_save) { _, _ ->
                runtime.saveVolumeRule(existing?.copy(percent = gain.progress) ?: LocalVolumeRule(events[trigger.selectedItemPosition], gain.progress))
            }.show()
    }

    private fun eventName(event: OutputEvent) = activity.getString(when (event) {
        OutputEvent.BLUETOOTH_CONNECTED -> R.string.rule_bluetooth_on
        OutputEvent.BLUETOOTH_DISCONNECTED -> R.string.rule_bluetooth_off
        OutputEvent.WIRED_CONNECTED -> R.string.rule_wired_on
        OutputEvent.WIRED_DISCONNECTED -> R.string.rule_wired_off
        OutputEvent.USB_CONNECTED -> R.string.rule_usb_on
        OutputEvent.USB_DISCONNECTED -> R.string.rule_usb_off
    })

    private fun resultLabel(result: RuleResult) = when (result) {
        RuleResult.APPLIED -> R.string.rule_applied
        RuleResult.ALREADY_SET -> R.string.rule_already_set
        RuleResult.CONFLICT -> R.string.rule_conflict
        RuleResult.PERMISSION -> R.string.rule_permission
        RuleResult.FIXED -> R.string.rule_fixed
        RuleResult.REJECTED -> R.string.rule_rejected
    }
}
