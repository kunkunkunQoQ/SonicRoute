package org.sonicroute.core

import java.io.File

internal data class BusinessVector(val expected: String, val metadata: JsonObject, val frame: SrapFrame) {
    fun request() = SrapCodec.parseControl(frame)
    fun params() = SrapCodec.requiredObject(request().body, "params")
}

internal fun readBusinessVectors(path: String): Map<String, BusinessVector> {
    val vectors = mutableMapOf<String, BusinessVector>()
    fun bytes(hex: String): ByteArray {
        check(hex.length % 2 == 0 && hex.matches(Regex("[0-9a-f]+")))
        return hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }
    for (line in File(path).readLines(Charsets.UTF_8).filter { it.isNotBlank() && !it.startsWith("#") }) {
        val fields = line.split('|')
        check(fields.size == 5)
        val metadata = StrictJson.parse(fields[2]) as JsonObject
        val wire = bytes(fields[4])
        val input = wire.inputStream()
        val frame = requireNotNull(SrapCodec.readFrame(input))
        check(input.read() == -1 && SrapCodec.encode(frame).contentEquals(wire) && frame.payload.contentEquals(bytes(fields[3])))
        val vector = BusinessVector(fields[1], metadata, frame)
        if (frame.type == SrapFrameType.DATA) {
            check(vector.expected == "data" && SrapCodec.requiredLong(metadata, "streamId") == frame.streamId)
        } else {
            val envelope = vector.request()
            check(vector.expected in setOf("accept", "reject") && envelope.type == "request" &&
                envelope.id == "00000000-0000-0000-0000-000000000001" && envelope.session == "00000000000000000000000000000001")
            check(SrapCodec.requiredLong(envelope.body, "capabilityRevision") == 7L &&
                envelope.body["op"] == metadata["op"] && envelope.body["params"] == metadata["params"])
        }
        check(vectors.put(fields[0], vector) == null)
    }
    check(vectors.keys == setOf("file.binary", "display.annexb", "file.unsafe_name", "volume.index", "volume.mixed", "volume.scope"))
    return vectors
}
