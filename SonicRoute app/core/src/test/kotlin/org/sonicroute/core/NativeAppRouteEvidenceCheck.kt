package org.sonicroute.core

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream

fun main() {
    val baseline = mapOf(21 to setOf(4), 22 to setOf(7, 8))
    val route = NativeAppRouteEvidence(baseline, 1)
    check(!route.isOnTarget(baseline))
    check(route.isOnTarget(mapOf(21 to setOf(1), 22 to setOf(1), 23 to setOf(1))))
    check(!route.isOnTarget(mapOf(21 to setOf(1), 22 to setOf(7))))
    check(!route.isOnTarget(mapOf(21 to setOf(1, 4))))
    check(!route.isOnTarget(mapOf(22 to emptySet())))
    check(!route.isOnTarget(mapOf(35 to setOf(1))))
    check(!route.isOnTarget(emptyMap()))
    check(route.isRestored(baseline))
    check(route.isRestored(mapOf(22 to setOf(8, 7))))
    check(!route.isRestored(mapOf(22 to setOf(7))))
    check(!route.isRestored(mapOf(21 to setOf(1))))
    check(!route.isRestored(mapOf(35 to setOf(4))))
    check(!route.isRestored(mapOf(21 to setOf(4), 35 to setOf(1))))
    check(runCatching { NativeAppRouteEvidence(mapOf(21 to setOf(0)), 1) }.isFailure)
    check(runCatching { NativeAppRouteEvidence(mapOf(0 to setOf(1)), 1) }.isFailure)
    val wire = NativeAppRoutingPipe
    val apk = "/data/app/~~_x1=-/org.sonicroute.app-Q123==/base.apk"
    check(wire.validApk(apk, apk))
    for (bad in listOf("/sdcard/base.apk", "/data/app/a b/base.apk", "/data/app/../x/base.apk",
        "/data/app/a\nB/base.apk", "/data/app/a;B/base.apk", "/data/app/a\$B/base.apk")) {
        check(!wire.validApk(bad, bad))
    }
    check(!wire.validApk(apk, "/data/app/something/base.apk"))
    check(wire.validPackage("com.example.music") && !wire.validPackage("com.example music"))
    val nonce = wire.random()
    val signers = wire.random()
    check(wire.rootProof(0, nonce, nonce, signers, signers))
    check(!wire.rootProof(2000, nonce, nonce, signers, signers))
    check(!wire.rootProof(0, nonce, signers, signers, signers))
    check(!wire.rootProof(0, nonce, nonce, signers, nonce))
    val body = ByteArrayOutputStream()
    DataOutputStream(body).use { wire.text(it, "output 好"); wire.ids(it, intArrayOf(1, 7)) }
    DataInputStream(ByteArrayInputStream(body.toByteArray())).use {
        check(wire.text(it) == "output 好" && wire.ids(it).contentEquals(intArrayOf(1, 7)) && it.read() == -1)
    }
    fun source(vararg bytes: Int) = DataInputStream(ByteArrayInputStream(bytes.map(Int::toByte).toByteArray()))
    for (bad in listOf(source(2, 1), source(0, 1, 0), source(0, 1, 255), source(0, 4, 65))) {
        check(runCatching { wire.text(bad) }.isFailure)
    }
    check(runCatching { wire.ids(source(33)) }.isFailure)
    check(runCatching { wire.ids(source(1, 0, 0, 0, 0)) }.isFailure)
    val address = "00:11:22:33:44:55"
    val key = checkNotNull(NativeSavedRoute.bondedOutput(8, address) { true })
    check(!key.contains(address) && NativeSavedRoute.bondedOutput(8, address) { false } == null)
    check(NativeSavedRoute.bondedOutput(8, "02:00:00:00:00:00") { true } == null)
    check(NativeSavedRoute.bondedOutput(8, "") { true } == null)
    val rule = NativeSavedRoute("com.sample.media", "Media", "Wireless", 8, "signed", key)
    check(rule.matches("com.sample.media", 8, "signed"))
    check(!rule.matches("com.sample.media", 8, "changed") && !rule.matches("com.sample.media", 9, "signed"))
    check(!rule.matches("com.other.media", 8, "signed"))
    check(rule.resolve(listOf(13 to key, 15 to "elsewhere")) { it.second }?.first == 13)
    check(rule.resolve(listOf(26 to key)) { it.second }?.first == 26)
    check(rule.resolve(listOf(1 to key, 2 to key)) { it.second } == null)
    check(rule.resolve(emptyList<Pair<Int, String>>()) { it.second } == null)
    val heading = "Type     Id Active Client(pid/uid) Session Port Id S  Flags   Format Chn mask  SRate ST Usg CT  G db  L dB  R dB  VS dB  PortVol dB  PortMuted"
    val legacyRow = "319    yes   1107/  10777 60801   245 A  0x000 00000001 00000001  22050  3   1  0    -3     0     0     0           -3      false"
    val lines = listOf("Output thread 0xf0, name AudioOut_A, tid 741, type 0 (MIXER):", " Standby: no",
        " Output devices: 0x80 (AUDIO_DEVICE_OUT_BLUETOOTH_A2DP)", "  2 Tracks of which 1 are active", heading, legacyRow,
        legacyRow.replace("10777", "10778"), legacyRow.replace(" yes ", " no "), "Input thread 0xbe, name AudioIn_D, tid 1942, type 1 (RECORD):", heading, legacyRow)
    val physical = NativeFlingerTrack.read(lines.asSequence(), 10777, setOf(60801))
    check(physical[60801] == listOf(NativeFlingerTrack(3, 1, setOf("AUDIO_DEVICE_OUT_BLUETOOTH_A2DP"))))
    val dump = lines.joinToString("\n")
    check(NativeFlingerTrack.read(dump.reader(), 10777, setOf(60801)) == physical)
    check(NativeFlingerTrack.read((dump + "\n" + "z".repeat(65537)).reader(), 10777, setOf(60801)).isEmpty())
    check(NativeFlingerTrack.confirmOutputs(setOf(5), setOf(7), true).isEmpty())
    check(NativeFlingerTrack.confirmOutputs(setOf(5), emptySet(), true).isEmpty())
    check(NativeFlingerTrack.confirmOutputs(setOf(5), emptySet(), false) == setOf(5))
    check(NativeFlingerTrack.confirmOutputs(emptySet(), setOf(7), true) == setOf(7))
    check(NativeFlingerTrack.confirmOutputs(setOf(5, 7), setOf(7), true).isEmpty())
    check(NativeFlingerTrack.read(lines.map { it.replace("Standby: no", "Standby: yes") }.asSequence(), 10777, setOf(60801)).isEmpty())
    check(NativeFlingerTrack.read(lines.asSequence(), 10777, setOf(60802)).isEmpty())
    val calls = mutableListOf<String>()
    fun checkProbe(reg: () -> Int, drop: (() -> Boolean)?, sample: (Int) -> String = { "sample:$it" }) =
        runCatching { NativeProbePolicy.inspect({ calls += "register"; reg() },
            drop?.let { release -> { calls += "release"; release() } }, { status -> calls += "sample"; sample(status) }) }
    check(checkProbe({ 0 }, null).exceptionOrNull() is NativeProbePolicy.ReleaseFailure && calls.isEmpty())
    check(checkProbe({ 0 }, { true }).getOrThrow() == "sample:0" && calls == listOf("register", "sample", "release"))
    calls.clear()
    check(checkProbe({ -1 }, { true }).getOrThrow() == "sample:-1" && calls == listOf("register", "sample"))
    calls.clear()
    check(checkProbe({ error("registration reply lost") }, { true }).isFailure && calls == listOf("register", "release"))
    calls.clear()
    check(checkProbe({ 0 }, { true }) { error("read failed") }.isFailure && calls == listOf("register", "sample", "release"))
    for (release in listOf<() -> Boolean>({ false }, { error("backend died") })) {
        calls.clear()
        check(checkProbe({ 0 }, release).exceptionOrNull() is NativeProbePolicy.ReleaseFailure)
        check(calls == listOf("register", "sample", "release"))
    }
    println("Native app route checks passed: live player identity, physical route and restore, signed root endpoint, bounded IPC, and verified probe release.")
}
