package org.sonicroute.core

import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction

class StrictJsonException(message: String, cause: Throwable? = null) : IllegalArgumentException(message, cause)

sealed interface JsonValue

data class JsonObject(val fields: Map<String, JsonValue>) : JsonValue {
    operator fun get(key: String): JsonValue? = fields[key]
    fun has(key: String): Boolean = fields.containsKey(key)
}

data class JsonArray(val items: List<JsonValue>) : JsonValue

data class JsonString(val value: String) : JsonValue

data class JsonNumber(val raw: String) : JsonValue {
    fun toLongExactOrNull(): Long? {
        if (raw.indexOf('.') >= 0 || raw.indexOf('e') >= 0 || raw.indexOf('E') >= 0) return null
        return raw.toLongOrNull()
    }

    fun toIntExactOrNull(): Int? {
        val v = toLongExactOrNull() ?: return null
        return if (v in Int.MIN_VALUE..Int.MAX_VALUE) v.toInt() else null
    }
}

data class JsonBool(val value: Boolean) : JsonValue

data object JsonNull : JsonValue

/**
 * Self-authored strict RFC 8259 JSON codec with zero third-party dependencies.
 * Rejects invalid UTF-8 bytes, duplicate keys at any nesting level, invalid numbers,
 * unescaped control characters, trailing tokens, and nesting depth exceeding maxDepth (16).
 */
object StrictJson {
    const val DEFAULT_MAX_DEPTH = 16

    fun parseUtf8(bytes: ByteArray, maxDepth: Int = DEFAULT_MAX_DEPTH): JsonValue {
        val text = decodeStrictUtf8(bytes)
        return parse(text, maxDepth)
    }

    fun decodeStrictUtf8(bytes: ByteArray): String {
        val decoder = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        return try {
            decoder.decode(ByteBuffer.wrap(bytes)).toString()
        } catch (e: CharacterCodingException) {
            throw StrictJsonException("Invalid UTF-8 byte sequence", e)
        }
    }

    fun parse(text: String, maxDepth: Int = DEFAULT_MAX_DEPTH): JsonValue {
        val parser = Parser(text, maxDepth)
        val value = parser.parseRoot()
        return value
    }

    fun encode(value: JsonValue): String {
        val sb = StringBuilder(128)
        writeValue(sb, value)
        return sb.toString()
    }

    fun encodeUtf8(value: JsonValue): ByteArray = encode(value).toByteArray(Charsets.UTF_8)

    fun obj(vararg pairs: Pair<String, JsonValue?>): JsonObject {
        val map = LinkedHashMap<String, JsonValue>(pairs.size)
        for ((k, v) in pairs) {
            if (v != null) map[k] = v
        }
        return JsonObject(map)
    }

    fun arr(items: List<JsonValue>): JsonArray = JsonArray(items)
    fun str(value: String): JsonString = JsonString(value)
    fun num(value: Long): JsonNumber = JsonNumber(value.toString())
    fun num(value: Int): JsonNumber = JsonNumber(value.toString())
    fun bool(value: Boolean): JsonBool = JsonBool(value)

    private fun writeValue(sb: StringBuilder, value: JsonValue) {
        when (value) {
            is JsonObject -> {
                sb.append('{')
                var first = true
                for ((k, v) in value.fields) {
                    if (!first) sb.append(',')
                    first = false
                    writeString(sb, k)
                    sb.append(':')
                    writeValue(sb, v)
                }
                sb.append('}')
            }
            is JsonArray -> {
                sb.append('[')
                for (i in value.items.indices) {
                    if (i > 0) sb.append(',')
                    writeValue(sb, value.items[i])
                }
                sb.append(']')
            }
            is JsonString -> writeString(sb, value.value)
            is JsonNumber -> sb.append(value.raw)
            is JsonBool -> sb.append(if (value.value) "true" else "false")
            JsonNull -> sb.append("null")
        }
    }

    private fun writeString(sb: StringBuilder, value: String) {
        sb.append('"')
        for (ch in value) {
            when (ch) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> if (ch.code < 0x20) {
                    sb.append("\\u")
                    sb.append(ch.code.toString(16).padStart(4, '0'))
                } else {
                    sb.append(ch)
                }
            }
        }
        sb.append('"')
    }

    private class Parser(private val src: String, private val maxDepth: Int) {
        private var pos = 0

        fun parseRoot(): JsonValue {
            skipWhitespace()
            val value = parseValue(0)
            skipWhitespace()
            if (pos != src.length) throw StrictJsonException("Unexpected trailing JSON token at $pos")
            return value
        }

        private fun parseValue(depth: Int): JsonValue {
            if (pos >= src.length) throw StrictJsonException("Unexpected end of JSON")
            return when (src[pos]) {
                '{' -> parseObject(depth + 1)
                '[' -> parseArray(depth + 1)
                '"' -> JsonString(parseString())
                't' -> parseLiteral("true", JsonBool(true))
                'f' -> parseLiteral("false", JsonBool(false))
                'n' -> parseLiteral("null", JsonNull)
                '-', in '0'..'9' -> parseNumber()
                else -> throw StrictJsonException("Unexpected token '${src[pos]}' at $pos")
            }
        }

        private fun parseObject(depth: Int): JsonObject {
            if (depth > maxDepth) throw StrictJsonException("JSON nesting exceeds max depth $maxDepth")
            pos++ // '{'
            skipWhitespace()
            val map = LinkedHashMap<String, JsonValue>()
            if (pos < src.length && src[pos] == '}') {
                pos++
                return JsonObject(map)
            }
            while (true) {
                skipWhitespace()
                if (pos >= src.length || src[pos] != '"') {
                    throw StrictJsonException("Expected JSON string key at $pos")
                }
                val key = parseString()
                if (map.containsKey(key)) {
                    throw StrictJsonException("Duplicate JSON key: $key")
                }
                skipWhitespace()
                if (pos >= src.length || src[pos] != ':') {
                    throw StrictJsonException("Expected ':' after key at $pos")
                }
                pos++
                skipWhitespace()
                map[key] = parseValue(depth)
                skipWhitespace()
                if (pos >= src.length) throw StrictJsonException("Unterminated JSON object")
                when (src[pos]) {
                    ',' -> pos++
                    '}' -> {
                        pos++
                        return JsonObject(map)
                    }
                    else -> throw StrictJsonException("Expected ',' or '}' at $pos")
                }
            }
        }

        private fun parseArray(depth: Int): JsonArray {
            if (depth > maxDepth) throw StrictJsonException("JSON nesting exceeds max depth $maxDepth")
            pos++ // '['
            skipWhitespace()
            val list = ArrayList<JsonValue>()
            if (pos < src.length && src[pos] == ']') {
                pos++
                return JsonArray(list)
            }
            while (true) {
                skipWhitespace()
                list += parseValue(depth)
                skipWhitespace()
                if (pos >= src.length) throw StrictJsonException("Unterminated JSON array")
                when (src[pos]) {
                    ',' -> pos++
                    ']' -> {
                        pos++
                        return JsonArray(list)
                    }
                    else -> throw StrictJsonException("Expected ',' or ']' at $pos")
                }
            }
        }

        private fun parseString(): String {
            pos++ // '"'
            val sb = StringBuilder()
            while (pos < src.length) {
                val ch = src[pos++]
                when {
                    ch == '"' -> return sb.toString()
                    ch == '\\' -> {
                        if (pos >= src.length) throw StrictJsonException("Unterminated escape sequence")
                        when (val esc = src[pos++]) {
                            '"' -> sb.append('"')
                            '\\' -> sb.append('\\')
                            '/' -> sb.append('/')
                            'b' -> sb.append('\b')
                            'f' -> sb.append('\u000C')
                            'n' -> sb.append('\n')
                            'r' -> sb.append('\r')
                            't' -> sb.append('\t')
                            'u' -> {
                                if (pos + 4 > src.length) throw StrictJsonException("Incomplete \\u escape")
                                val hex = src.substring(pos, pos + 4)
                                val code = hex.toIntOrNull(16)
                                    ?: throw StrictJsonException("Invalid \\u escape: $hex")
                                pos += 4
                                sb.append(code.toChar())
                            }
                            else -> throw StrictJsonException("Invalid escape character '\\$esc'")
                        }
                    }
                    ch.code < 0x20 -> throw StrictJsonException("Unescaped control character in string")
                    else -> sb.append(ch)
                }
            }
            throw StrictJsonException("Unterminated JSON string")
        }

        private fun parseNumber(): JsonNumber {
            val start = pos
            if (src[pos] == '-') pos++
            if (pos >= src.length) throw StrictJsonException("Incomplete number at $start")
            when (src[pos]) {
                '0' -> pos++
                in '1'..'9' -> {
                    pos++
                    while (pos < src.length && src[pos] in '0'..'9') pos++
                }
                else -> throw StrictJsonException("Invalid number at $start")
            }
            if (pos < src.length && src[pos] == '.') {
                pos++
                if (pos >= src.length || src[pos] !in '0'..'9') {
                    throw StrictJsonException("Invalid fractional number at $start")
                }
                while (pos < src.length && src[pos] in '0'..'9') pos++
            }
            if (pos < src.length && (src[pos] == 'e' || src[pos] == 'E')) {
                pos++
                if (pos < src.length && (src[pos] == '+' || src[pos] == '-')) pos++
                if (pos >= src.length || src[pos] !in '0'..'9') {
                    throw StrictJsonException("Invalid exponent at $start")
                }
                while (pos < src.length && src[pos] in '0'..'9') pos++
            }
            val raw = src.substring(start, pos)
            val parsed = raw.toDoubleOrNull()
            if (parsed == null || parsed.isNaN() || parsed.isInfinite()) {
                throw StrictJsonException("Out-of-range JSON number: $raw")
            }
            return JsonNumber(raw)
        }

        private fun parseLiteral(expected: String, result: JsonValue): JsonValue {
            if (!src.startsWith(expected, pos)) {
                throw StrictJsonException("Unexpected token at $pos")
            }
            pos += expected.length
            return result
        }

        private fun skipWhitespace() {
            while (pos < src.length) {
                when (src[pos]) {
                    ' ', '\t', '\r', '\n' -> pos++
                    else -> return
                }
            }
        }
    }
}
