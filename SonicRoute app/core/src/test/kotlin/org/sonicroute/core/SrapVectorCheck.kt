package org.sonicroute.core

import java.io.ByteArrayInputStream
import java.io.EOFException
import java.io.File
import java.io.FilterInputStream

/**
 * Full JVM check of the shared SRAP v1.0 golden vectors (docs/protocol-fixtures/srap-v1.txt)
 * using our production SrapCodec + StrictJson + hello/capabilities decoders.
 */
fun main(args: Array<String>) {
    var count = 0
    for (line in File(args.single()).readLines().filter { it.isNotBlank() && !it.startsWith("#") }) {
        val fields = line.split('|', limit = 7)
        val name = fields[0]
        val expectedType = fields[1].toInt()
        val expectedStreamId = fields[2].toLong()
        val chunk = fields[3].toInt()
        val expectedResult = fields[4]
        val expectedPayload = fields[5].encodeToByteArray()
        val wire = fields[6].chunked(2).map { it.toInt(16).toByte() }.toByteArray()

        val fragmented = object : FilterInputStream(ByteArrayInputStream(wire)) {
            override fun read(target: ByteArray, offset: Int, length: Int): Int =
                super.read(target, offset, minOf(chunk, length))
        }

        val actual = fragmented.use { input ->
            try {
                val frame = SrapCodec.readFrame(input) ?: error("Unexpected clean EOF on $name")
                check(frame.type.wireByte.toInt() == expectedType) { "$name type mismatch" }
                check(frame.streamId == expectedStreamId) { "$name streamId mismatch" }
                check(frame.payload.contentEquals(expectedPayload)) { "$name payload mismatch" }
                val reencoded = SrapCodec.encode(frame)
                check(reencoded.contentEquals(wire)) { "$name lossless encode mismatch" }

                if (frame.type == SrapFrameType.DATA) {
                    "data"
                } else {
                    try {
                        val env = SrapCodec.parseControl(frame)
                        when (env.type) {
                            "hello" -> {
                                val hello = SrapCodec.parseHello(env.body)
                                if (SrapCodec.negotiateMinor(hello) == 0) "hello" else "version_mismatch"
                            }
                            "capabilities" -> {
                                val caps = SrapCodec.parseCapabilitiesBody(env.body)
                                val ok = caps.capabilityRevision == 7L &&
                                    caps.entries.size == 3 &&
                                    caps.entries[Capability.ROUTE_PLAYBACK] == CapabilityEntry(
                                        CapabilityStatus.AVAILABLE,
                                        scope = "owned_track"
                                    ) &&
                                    caps.entries[Capability.ROUTE_OTHER_APPS] == CapabilityEntry(
                                        CapabilityStatus.NOT_IMPLEMENTED
                                    ) &&
                                    caps.entries[Capability.EXCLUSIVE_CAPTURE] == CapabilityEntry(
                                        CapabilityStatus.UNAVAILABLE
                                    )
                                if (ok) "unknown_ignored" else "bad_capabilities"
                            }
                            else -> "control"
                        }
                    } catch (_: SrapProtocolException) {
                        "invalid_json"
                    }
                }
            } catch (_: SrapProtocolException) {
                "invalid_data"
            } catch (_: EOFException) {
                "short_read"
            }
        }
        check(actual == expectedResult) { "$name expected $expectedResult, got $actual" }
        count++
    }
    check(count == 11) { "Expected 11 golden vectors, ran $count" }

    // Maximum legal payload round-trip (65536 bytes).
    val maxFrame = SrapFrame(SrapFrameType.DATA, 1L, ByteArray(SrapCodec.MAX_PAYLOAD))
    check(SrapCodec.encode(maxFrame).size == SrapCodec.HEADER_SIZE + SrapCodec.MAX_PAYLOAD)

    // Deep JSON (>16) and invalid UTF-8 rejection.
    val tooDeep = "{\"a\":".repeat(17) + "1" + "}".repeat(17)
    check(runCatching { StrictJson.parse(tooDeep) }.isFailure)
    val badUtf8 = byteArrayOf(0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF.toByte(), 0x22, 0x7D)
    check(runCatching { StrictJson.parseUtf8(badUtf8) }.isFailure)

    println("PASS $count SRAP v1.0 full Kotlin framing + strict JSON + capability vectors")
}
