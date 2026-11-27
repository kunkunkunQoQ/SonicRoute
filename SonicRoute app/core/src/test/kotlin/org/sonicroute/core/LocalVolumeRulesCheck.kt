package org.sonicroute.core

fun main() {
    val bt = AudioOutputKind.BLUETOOTH
    val usb = AudioOutputKind.USB
    val arrived = OutputEvent.BLUETOOTH_CONNECTED
    val removed = OutputEvent.BLUETOOTH_DISCONNECTED
    val usbArrived = OutputEvent.USB_CONNECTED
    val rules = LocalVolumeRules(listOf(LocalVolumeRule(arrived, 40), LocalVolumeRule(removed, 15)))
    val writes = mutableListOf<Int>()
    fun observe(vararg kinds: AudioOutputKind) = rules.onOutputs(kinds.toSet(), 123) {
        writes += it
        RuleResult.APPLIED
    }
    check(!observe(bt))
    check(!observe(bt))
    check(observe() && writes == listOf(15))
    check(observe(bt) && writes == listOf(15, 40))
    check(!observe(bt, usb))
    rules.resetBaseline()
    check(!observe())
    rules.set(LocalVolumeRule(usbArrived, 50))
    check(observe(bt, usb) && writes == listOf(15, 40))
    check(rules.snapshot().filter { it.event.connected }.all { !it.enabled && it.lastResult == RuleResult.CONFLICT })
    val restored = LocalVolumeRules(rules.snapshot())
    check(!restored.onOutputs(setOf(bt, usb), 1) { error("must not replay") })
    check(restored.onOutputs(emptySet(), 2) { RuleResult.PERMISSION })
    check(restored.snapshot().single { it.event == removed }.let { !it.enabled && it.lastResult == RuleResult.PERMISSION })
    check(!restored.onOutputs(setOf(bt), 3) { error("paused rule ran") })
    restored.set(LocalVolumeRule(removed, 20))
    check(restored.onOutputs(emptySet(), 4) { RuleResult.ALREADY_SET })
    check(restored.snapshot().single { it.event == removed }.enabled)
    restored.set(LocalVolumeRule(arrived, 20))
    restored.set(LocalVolumeRule(usbArrived, 20))
    var requests = 0
    check(restored.onOutputs(setOf(bt, usb), 5) { requests++; RuleResult.FIXED })
    check(requests == 1)
    restored.remove(arrived)
    check(restored.snapshot().none { it.event == arrived })
    println("Local volume rules passed")
}
