package org.sonicroute.core

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

class SrapProtocolException(message: String, cause: Throwable? = null) : IllegalArgumentException(message, cause)

enum class SrapFrameType(val wireByte: Byte) {
    CONTROL(1),
    DATA(2);

    companion object {
        fun fromWireByte(b: Byte): SrapFrameType? = when (b.toInt()) {
            1 -> CONTROL
            2 -> DATA
            else -> null
        }
    }
}

class SrapFrame(
    val type: SrapFrameType,
    val streamId: Long,
    val payload: ByteArray
)

data class SrapControlEnvelope(
    val type: String,
    val body: JsonObject,
    val id: String? = null,
    val session: String? = null,
    val seq: Long? = null
)

data class SrapHello(
    val protocol: String,
    val major: Int,
    val minorMin: Int,
    val minorMax: Int,
    val appVersion: String? = null
)

data class SrapCapabilitiesMessage(
    val capabilityRevision: Long,
    val entries: Map<Capability, CapabilityEntry>
)

/**
 * SRAP v1.0 binary framing and strict JSON control codec matching SonicRoute.AndroidAudio/SrapProtocol.cs
 * and docs/protocol.md byte-for-byte.
 */
object SrapCodec {
    const val MAGIC = 0x53524150 // ASCII "SRAP"
    const val HEADER_SIZE = 16
    const val MAX_PAYLOAD = 64 * 1024
    const val PROTOCOL_NAME = "sonicroute.android"
    const val MAJOR = 1
    const val MINOR_MIN = 0
    const val MINOR_MAX = 0

    private val UUID_LOWER_REGEX = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
    private val SESSION_HEX_REGEX = Regex("^[0-9a-f]{32}$")

    fun validateFrameHeader(type: SrapFrameType, streamId: Long, length: Long) {
        if (streamId < 0L || streamId > 0xFFFFFFFFL) {
            throw SrapProtocolException("SRAP streamId out of uint32 range: $streamId")
        }
        if (length !in 1L..MAX_PAYLOAD.toLong()) {
            throw SrapProtocolException("SRAP payload length out of range: $length")
        }
        when (type) {
            SrapFrameType.CONTROL -> if (streamId != 0L) {
                throw SrapProtocolException("SRAP control frame requires streamId=0, got $streamId")
            }
            SrapFrameType.DATA -> if (streamId == 0L) {
                throw SrapProtocolException("SRAP data frame requires non-zero streamId")
            }
        }
    }

    fun encode(frame: SrapFrame): ByteArray {
        validateFrameHeader(frame.type, frame.streamId, frame.payload.size.toLong())
        val out = ByteArray(HEADER_SIZE + frame.payload.size)
        val buf = ByteBuffer.wrap(out).order(ByteOrder.BIG_ENDIAN)
        buf.putInt(MAGIC)
        buf.put(frame.type.wireByte)
        buf.put(0) // flags = 0
        buf.putShort(0) // reserved = 0
        buf.putInt(frame.streamId.toInt())
        buf.putInt(frame.payload.size)
        System.arraycopy(frame.payload, 0, out, HEADER_SIZE, frame.payload.size)
        return out
    }

    fun writeFrame(output: OutputStream, frame: SrapFrame) {
        output.write(encode(frame))
        output.flush()
    }

    /**
     * Reads one complete SRAP frame from an arbitrary fragmented stream.
     * Returns null on clean EOF before the first header byte; throws EOFException on truncated frame.
     */
    fun readFrame(input: InputStream): SrapFrame? {
        val header = ByteArray(HEADER_SIZE)
        val first = input.read(header, 0, 1)
        if (first < 0) return null
        if (first == 0) readFully(input, header, 0, HEADER_SIZE) else readFully(input, header, 1, HEADER_SIZE - 1)

        val buf = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN)
        val magic = buf.int
        val typeByte = buf.get()
        val flags = buf.get()
        val reserved = buf.short
        val streamId = buf.int.toLong() and 0xFFFFFFFFL
        val length = buf.int.toLong() and 0xFFFFFFFFL

        if (magic != MAGIC || flags.toInt() != 0 || reserved.toInt() != 0) {
            throw SrapProtocolException("Invalid SRAP frame header")
        }
        val type = SrapFrameType.fromWireByte(typeByte)
            ?: throw SrapProtocolException("Unknown SRAP frame type: $typeByte")
        validateFrameHeader(type, streamId, length)

        val payload = ByteArray(length.toInt())
        readFully(input, payload, 0, payload.size)
        return SrapFrame(type, streamId, payload)
    }

    private fun readFully(input: InputStream, target: ByteArray, offset: Int, length: Int) {
        var read = 0
        while (read < length) {
            val n = input.read(target, offset + read, length - read)
            if (n < 0) throw EOFException("Truncated SRAP frame ($read/$length bytes)")
            read += n
        }
    }

    fun parseControl(frame: SrapFrame): SrapControlEnvelope {
        if (frame.type != SrapFrameType.CONTROL) {
            throw SrapProtocolException("Expected SRAP control frame")
        }
        val root = try {
            StrictJson.parseUtf8(frame.payload, maxDepth = StrictJson.DEFAULT_MAX_DEPTH)
        } catch (e: StrictJsonException) {
            throw SrapProtocolException("Invalid SRAP control JSON: ${e.message}", e)
        }
        if (root !is JsonObject) {
            throw SrapProtocolException("SRAP control payload must be a JSON object")
        }
        val type = requiredText(root, "type")
        val body = requiredObject(root, "body")
        val id = optionalText(root, "id")?.also {
            if (!UUID_LOWER_REGEX.matches(it)) {
                throw SrapProtocolException("Invalid SRAP request id UUID format")
            }
        }
        val session = optionalText(root, "session")?.also {
            if (!SESSION_HEX_REGEX.matches(it)) {
                throw SrapProtocolException("Invalid SRAP session format")
            }
        }
        val seq = if (root.has("seq")) {
            val raw = root["seq"] as? JsonNumber
                ?: throw SrapProtocolException("Invalid SRAP seq field")
            val parsed = raw.toLongExactOrNull()
                ?: throw SrapProtocolException("Invalid SRAP seq integer")
            if (parsed < 0L) throw SrapProtocolException("Negative SRAP seq")
            parsed
        } else null
        return SrapControlEnvelope(type = type, body = body, id = id, session = session, seq = seq)
    }

    fun encodeControl(envelope: SrapControlEnvelope): SrapFrame {
        require(envelope.type.length in 1..128)
        envelope.id?.let { require(UUID_LOWER_REGEX.matches(it)) { "id must be lowercase UUID" } }
        envelope.session?.let { require(SESSION_HEX_REGEX.matches(it)) { "session must be 32 lowercase hex" } }
        envelope.seq?.let { require(it >= 0L) { "seq must be non-negative" } }

        val obj = StrictJson.obj(
            "type" to StrictJson.str(envelope.type),
            "id" to envelope.id?.let { StrictJson.str(it) },
            "session" to envelope.session?.let { StrictJson.str(it) },
            "seq" to envelope.seq?.let { StrictJson.num(it) },
            "body" to envelope.body
        )
        return SrapFrame(SrapFrameType.CONTROL, 0L, StrictJson.encodeUtf8(obj))
    }

    fun parseHello(body: JsonObject): SrapHello {
        val protocol = requiredText(body, "protocol")
        val major = requiredVersionInt(body, "major")
        val minorMin = requiredVersionInt(body, "minorMin")
        val minorMax = requiredVersionInt(body, "minorMax")
        if (minorMax < minorMin) throw SrapProtocolException("Invalid SRAP version range")
        val appVersion = optionalText(body, "appVersion")
        return SrapHello(protocol, major, minorMin, minorMax, appVersion)
    }

    fun encodeHello(
        major: Int = MAJOR,
        minorMin: Int = MINOR_MIN,
        minorMax: Int = MINOR_MAX,
        appVersion: String? = null
    ): JsonObject = StrictJson.obj(
        "protocol" to StrictJson.str(PROTOCOL_NAME),
        "major" to StrictJson.num(major),
        "minorMin" to StrictJson.num(minorMin),
        "minorMax" to StrictJson.num(minorMax),
        "appVersion" to appVersion?.takeIf { it.length in 1..128 }?.let { StrictJson.str(it) }
    )

    fun negotiateMinor(
        peer: SrapHello,
        localMajor: Int = MAJOR,
        localMinorMin: Int = MINOR_MIN,
        localMinorMax: Int = MINOR_MAX
    ): Int? {
        if (peer.protocol != PROTOCOL_NAME || peer.major != localMajor) return null
        val low = maxOf(localMinorMin, peer.minorMin)
        val high = minOf(localMinorMax, peer.minorMax)
        return if (low <= high) high else null
    }

    fun encodeCapabilitiesBody(snapshot: PermissionSnapshot): JsonObject {
        val entries = Capability.entries.map { cap ->
            val e = snapshot.entry(cap)
            val limitsObj = if (e.limits.isEmpty()) null else JsonObject(
                e.limits.entries.associate { (k, v) -> k.take(128) to StrictJson.str(v.take(128)) }
            )
            StrictJson.obj(
                "id" to StrictJson.str(cap.wireId),
                "state" to StrictJson.str(e.status.wireId),
                "reason" to e.reason?.takeIf { it.isNotEmpty() }?.take(128)?.let { StrictJson.str(it) },
                "scope" to e.scope?.takeIf { it.isNotEmpty() }?.take(128)?.let { StrictJson.str(it) },
                "limits" to limitsObj
            )
        }
        return StrictJson.obj(
            "capabilityRevision" to StrictJson.num(snapshot.capabilityRevision),
            "entries" to StrictJson.arr(entries)
        )
    }

    fun parseCapabilitiesBody(body: JsonObject): SrapCapabilitiesMessage {
        val revRaw = body["capabilityRevision"] as? JsonNumber
            ?: throw SrapProtocolException("Missing or invalid capabilityRevision")
        val revision = revRaw.toLongExactOrNull()
            ?: throw SrapProtocolException("Invalid capabilityRevision integer")
        if (revision < 0L) throw SrapProtocolException("Negative capabilityRevision")
        val entriesArr = body["entries"] as? JsonArray
            ?: throw SrapProtocolException("Missing or invalid entries array")

        val seen = HashSet<String>()
        val parsed = LinkedHashMap<Capability, CapabilityEntry>()
        for (item in entriesArr.items) {
            val obj = item as? JsonObject
                ?: throw SrapProtocolException("Capability entry must be an object")
            val id = requiredText(obj, "id")
            if (!seen.add(id)) throw SrapProtocolException("Duplicate SRAP capability: $id")
            val capability = Capability.fromWireId(id) ?: continue // Ignore unknown capabilities
            val stateStr = requiredText(obj, "state")
            val status = CapabilityStatus.fromWireId(stateStr) ?: continue // Ignore unknown states
            val reason = optionalText(obj, "reason")
            val scope = optionalText(obj, "scope")
            val limits = if (obj.has("limits")) {
                val limObj = requiredObject(obj, "limits")
                val map = LinkedHashMap<String, String>(limObj.fields.size)
                for ((k, _) in limObj.fields) {
                    map[k] = requiredText(limObj, k)
                }
                map
            } else emptyMap()
            parsed[capability] = CapabilityEntry(status = status, reason = reason, scope = scope, limits = limits)
        }
        return SrapCapabilitiesMessage(revision, parsed)
    }

    fun requiredObject(obj: JsonObject, key: String): JsonObject =
        obj[key] as? JsonObject ?: throw SrapProtocolException("Missing or invalid SRAP object field: $key")

    fun requiredText(obj: JsonObject, key: String, maxLen: Int = 128): String {
        val s = (obj[key] as? JsonString)?.value
            ?: throw SrapProtocolException("Missing or invalid SRAP string field: $key")
        if (s.isEmpty() || s.length > maxLen) {
            throw SrapProtocolException("SRAP string field out of bounds: $key")
        }
        return s
    }

    fun optionalText(obj: JsonObject, key: String, maxLen: Int = 128): String? {
        if (!obj.has(key)) return null
        return requiredText(obj, key, maxLen)
    }

    fun requiredInt(obj: JsonObject, key: String): Int {
        val n = obj[key] as? JsonNumber ?: throw SrapProtocolException("Missing or invalid int field: $key")
        return n.toIntExactOrNull() ?: throw SrapProtocolException("Invalid int field: $key")
    }

    fun requiredLong(obj: JsonObject, key: String): Long {
        val n = obj[key] as? JsonNumber ?: throw SrapProtocolException("Missing or invalid long field: $key")
        return n.toLongExactOrNull() ?: throw SrapProtocolException("Invalid long field: $key")
    }

    fun optionalBool(obj: JsonObject, key: String): Boolean? {
        if (!obj.has(key)) return null
        return (obj[key] as? JsonBool)?.value ?: throw SrapProtocolException("Invalid bool field: $key")
    }

    private fun requiredVersionInt(obj: JsonObject, key: String): Int {
        val v = requiredInt(obj, key)
        if (v !in 0..65535) throw SrapProtocolException("Version field $key out of range: $v")
        return v
    }
}
