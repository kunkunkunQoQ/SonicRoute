package org.sonicroute.core

enum class OutputEvent(val kind: AudioOutputKind, val connected: Boolean) {
    BLUETOOTH_CONNECTED(AudioOutputKind.BLUETOOTH, true),
    BLUETOOTH_DISCONNECTED(AudioOutputKind.BLUETOOTH, false),
    WIRED_CONNECTED(AudioOutputKind.WIRED, true),
    WIRED_DISCONNECTED(AudioOutputKind.WIRED, false),
    USB_CONNECTED(AudioOutputKind.USB, true),
    USB_DISCONNECTED(AudioOutputKind.USB, false)
}

enum class RuleResult { APPLIED, ALREADY_SET, CONFLICT, PERMISSION, FIXED, REJECTED }

data class LocalVolumeRule(
    val event: OutputEvent,
    val percent: Int,
    val enabled: Boolean = true,
    val lastResult: RuleResult? = null,
    val lastRunMillis: Long = 0
) {
    init { require(percent in 0..100) }
}

/** Each output class has one arrival and one departure rule. The first snapshot is a baseline. */
class LocalVolumeRules(initial: List<LocalVolumeRule> = emptyList()) {
    private val rules = initial.associateByTo(mutableMapOf()) { it.event }
    private var previous: Set<AudioOutputKind>? = null

    fun snapshot() = OutputEvent.entries.mapNotNull(rules::get)

    fun set(rule: LocalVolumeRule) { rules[rule.event] = rule }
    fun remove(event: OutputEvent) { rules.remove(event) }
    fun resetBaseline() { previous = null }

    fun onOutputs(
        current: Set<AudioOutputKind>,
        nowMillis: Long,
        applyVolume: (Int) -> RuleResult
    ): Boolean {
        val old = previous
        previous = current.toSet()
        if (old == null || old == current) return false
        val matches = rules.values.filter {
            val event = it.event
            it.enabled && (event.kind in current) == event.connected && (event.kind in old) != event.connected
        }
        if (matches.isEmpty()) return false
        val first = matches.first()
        val result = if (matches.any { it.percent != first.percent }) RuleResult.CONFLICT else applyVolume(first.percent)
        for (rule in matches) {
            rules[rule.event] = rule.copy(
                enabled = result == RuleResult.APPLIED || result == RuleResult.ALREADY_SET,
                lastResult = result,
                lastRunMillis = nowMillis
            )
        }
        return true
    }
}
