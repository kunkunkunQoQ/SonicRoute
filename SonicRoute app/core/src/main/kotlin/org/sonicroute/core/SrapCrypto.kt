package org.sonicroute.core

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.net.Inet6Address
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.KeyFactory
import java.security.AlgorithmParameters
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * Self-authored SRAP v1.0 pairing transcript, ECDSA P-256/SHA-256 verification,
 * unpadded Base64url, copyable invite URI, TLS hardening constants, and lightweight
 * ASN.1 DER X.509 self-signed certificate generator (used for JVM TLS tests and fallback).
 */
object SrapCrypto {
    private val secureRandom = SecureRandom()
    private val p256 by lazy {
        AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }
            .getParameterSpec(ECParameterSpec::class.java)
    }
    private val HEX_UPPER = "0123456789ABCDEF".toCharArray()
    private val HEX_LOWER = "0123456789abcdef".toCharArray()
    private val B64URL_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".toCharArray()
    private val B64URL_DECODE = IntArray(128) { -1 }.apply {
        for (i in B64URL_ALPHABET.indices) this[B64URL_ALPHABET[i].code] = i
    }

    val ALLOWED_TLS_PROTOCOLS = arrayOf("TLSv1.3", "TLSv1.2")
    val ALLOWED_TLS_CIPHERS = arrayOf(
        "TLS_AES_256_GCM_SHA384",
        "TLS_AES_128_GCM_SHA256",
        "TLS_CHACHA20_POLY1305_SHA256",
        "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384",
        "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256",
        "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256"
    )

    fun randomBytes(count: Int): ByteArray = ByteArray(count).also(secureRandom::nextBytes)

    fun randomSessionId(): String = toHexLower(randomBytes(16))

    fun sha256(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)

    fun sha256HexUpper(bytes: ByteArray): String = toHexUpper(sha256(bytes))

    fun toHexUpper(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xFF
            out[i * 2] = HEX_UPPER[v ushr 4]
            out[i * 2 + 1] = HEX_UPPER[v and 0x0F]
        }
        return String(out)
    }

    fun toHexLower(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xFF
            out[i * 2] = HEX_LOWER[v ushr 4]
            out[i * 2 + 1] = HEX_LOWER[v and 0x0F]
        }
        return String(out)
    }

    fun constantTimeEquals(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)

    fun base64UrlEncodeNoPad(data: ByteArray): String {
        val out = StringBuilder((data.size * 4 + 2) / 3)
        var i = 0
        while (i < data.size) {
            val b0 = data[i].toInt() and 0xFF
            val has1 = i + 1 < data.size
            val has2 = i + 2 < data.size
            val b1 = if (has1) data[i + 1].toInt() and 0xFF else 0
            val b2 = if (has2) data[i + 2].toInt() and 0xFF else 0
            out.append(B64URL_ALPHABET[b0 ushr 2])
            out.append(B64URL_ALPHABET[((b0 and 0x03) shl 4) or (b1 ushr 4)])
            if (has1) out.append(B64URL_ALPHABET[((b1 and 0x0F) shl 2) or (b2 ushr 6)])
            if (has2) out.append(B64URL_ALPHABET[b2 and 0x3F])
            i += 3
        }
        return out.toString()
    }

    fun base64UrlDecodeNoPad(text: String): ByteArray {
        if (text.isEmpty() || text.length % 4 == 1 || text.contains('=')) {
            throw SrapProtocolException("Invalid unpadded base64url length/padding")
        }
        val fullGroups = text.length / 4
        val rem = text.length % 4
        val outLen = fullGroups * 3 + when (rem) {
            0 -> 0
            2 -> 1
            3 -> 2
            else -> throw SrapProtocolException("Invalid base64url remainder")
        }
        val out = ByteArray(outLen)
        var inPos = 0
        var outPos = 0
        fun decodeChar(c: Char): Int {
            val code = c.code
            if (code >= 128 || B64URL_DECODE[code] < 0) {
                throw SrapProtocolException("Invalid base64url character")
            }
            return B64URL_DECODE[code]
        }
        repeat(fullGroups) {
            val v0 = decodeChar(text[inPos++])
            val v1 = decodeChar(text[inPos++])
            val v2 = decodeChar(text[inPos++])
            val v3 = decodeChar(text[inPos++])
            val triple = (v0 shl 18) or (v1 shl 12) or (v2 shl 6) or v3
            out[outPos++] = (triple ushr 16).toByte()
            out[outPos++] = (triple ushr 8).toByte()
            out[outPos++] = triple.toByte()
        }
        if (rem == 2) {
            val v0 = decodeChar(text[inPos++])
            val v1 = decodeChar(text[inPos])
            if ((v1 and 0x0F) != 0) throw SrapProtocolException("Non-canonical base64url trailing bits")
            out[outPos] = ((v0 shl 2) or (v1 ushr 4)).toByte()
        } else if (rem == 3) {
            val v0 = decodeChar(text[inPos++])
            val v1 = decodeChar(text[inPos++])
            val v2 = decodeChar(text[inPos])
            if ((v2 and 0x03) != 0) throw SrapProtocolException("Non-canonical base64url trailing bits")
            val val16 = (v0 shl 10) or (v1 shl 4) or (v2 ushr 2)
            out[outPos++] = (val16 ushr 8).toByte()
            out[outPos] = val16.toByte()
        }
        return out
    }

    /**
     * Exact SRAP 1.0 pairing signature transcript per docs/protocol.md:
     * ASCII "SRAP1/PAIR" || 0x00 || inviteId(16) || challenge(32) ||
     * androidSpkiSha256(32) || windowsSpkiSha256(32) || uint16BE(major) || uint16BE(minor).
     */
    fun buildPairingTranscript(
        inviteId: ByteArray,
        challenge: ByteArray,
        androidSpkiSha256: ByteArray,
        windowsSpkiSha256: ByteArray,
        major: Int,
        minor: Int
    ): ByteArray {
        require(inviteId.size == 16)
        require(challenge.size == 32)
        require(androidSpkiSha256.size == 32)
        require(windowsSpkiSha256.size == 32)
        require(major in 0..65535 && minor in 0..65535)
        val prefix = "SRAP1/PAIR".toByteArray(Charsets.US_ASCII)
        val out = ByteArray(prefix.size + 1 + 16 + 32 + 32 + 32 + 4)
        val buf = ByteBuffer.wrap(out).order(ByteOrder.BIG_ENDIAN)
        buf.put(prefix)
        buf.put(0)
        buf.put(inviteId)
        buf.put(challenge)
        buf.put(androidSpkiSha256)
        buf.put(windowsSpkiSha256)
        buf.putShort(major.toShort())
        buf.putShort(minor.toShort())
        return out
    }

    fun parseEcP256Spki(spkiDer: ByteArray): ECPublicKey {
        if (spkiDer.size !in 60..256) throw SrapProtocolException("Invalid EC SPKI length")
        val kf = KeyFactory.getInstance("EC")
        val pub = kf.generatePublic(X509EncodedKeySpec(spkiDer)) as? ECPublicKey
            ?: throw SrapProtocolException("Peer public key is not EC")
        val params = pub.params
        if (params.order != p256.order || params.curve != p256.curve ||
            params.generator != p256.generator || params.cofactor != p256.cofactor
        ) throw SrapProtocolException("Peer EC key must be P-256")
        return pub
    }

    fun signEcdsaP256Sha256(privateKey: PrivateKey, data: ByteArray): ByteArray {
        val sig = Signature.getInstance("SHA256withECDSA")
        sig.initSign(privateKey)
        sig.update(data)
        return sig.sign()
    }

    fun verifyEcdsaP256Sha256(spkiDer: ByteArray, data: ByteArray, derSignature: ByteArray): Boolean {
        if (derSignature.size !in 8..128) return false
        return runCatching {
            val pub = parseEcP256Spki(spkiDer)
            val sig = Signature.getInstance("SHA256withECDSA")
            sig.initVerify(pub)
            sig.update(data)
            sig.verify(derSignature)
        }.getOrDefault(false)
    }

    fun filterSupported(supported: Array<String>, allowed: Array<String>): Array<String> {
        val set = supported.toHashSet()
        return allowed.filter { set.contains(it) }.toTypedArray()
    }

    /**
     * Generates a minimal valid DER-encoded X.509 v3 self-signed certificate signed with SHA256withECDSA.
     * Used in JVM TLS tests without any BouncyCastle/third-party dependency.
     */
    fun generateSelfSignedEcCertificate(
        commonName: String,
        publicKey: PublicKey,
        privateKey: PrivateKey,
        validityDays: Long = 3650L
    ): X509Certificate {
        val nowMs = System.currentTimeMillis() - 60_000L
        val notAfterMs = nowMs + validityDays * 86_400_000L
        val serial = BigInteger(63, secureRandom).abs().max(BigInteger.ONE)

        val sha256WithEcdsaAlgId = derSeq(derOid(intArrayOf(1, 2, 840, 10045, 4, 3, 2)))
        val dn = derSeq(derSet(derSeq(derOid(intArrayOf(2, 5, 4, 3)), derUtf8(commonName))))
        val validity = derSeq(derUtcOrGenTime(Date(nowMs)), derUtcOrGenTime(Date(notAfterMs)))
        val version = derExplicit(0, derInteger(BigInteger.valueOf(2))) // v3
        val spki = publicKey.encoded

        val tbs = derSeq(version, derInteger(serial), sha256WithEcdsaAlgId, dn, validity, dn, spki)
        val signature = signEcdsaP256Sha256(privateKey, tbs)
        val certDer = derSeq(tbs, sha256WithEcdsaAlgId, derBitString(signature))

        val cf = CertificateFactory.getInstance("X.509")
        return cf.generateCertificate(ByteArrayInputStream(certDer)) as X509Certificate
    }

    private fun derTag(tag: Int, content: ByteArray): ByteArray {
        val out = ByteArrayOutputStream(content.size + 6)
        out.write(tag)
        val len = content.size
        when {
            len < 0x80 -> out.write(len)
            len <= 0xFF -> {
                out.write(0x81)
                out.write(len)
            }
            else -> {
                out.write(0x82)
                out.write(len ushr 8)
                out.write(len and 0xFF)
            }
        }
        out.write(content)
        return out.toByteArray()
    }

    private fun derSeq(vararg items: ByteArray): ByteArray {
        val body = ByteArrayOutputStream()
        for (item in items) body.write(item)
        return derTag(0x30, body.toByteArray())
    }

    private fun derSet(vararg items: ByteArray): ByteArray {
        val body = ByteArrayOutputStream()
        for (item in items) body.write(item)
        return derTag(0x31, body.toByteArray())
    }

    private fun derExplicit(tagNum: Int, inner: ByteArray): ByteArray = derTag(0xA0 or tagNum, inner)
    private fun derInteger(v: BigInteger): ByteArray = derTag(0x02, v.toByteArray())
    private fun derUtf8(s: String): ByteArray = derTag(0x0C, s.toByteArray(Charsets.UTF_8))
    private fun derBitString(bytes: ByteArray): ByteArray {
        val body = ByteArray(bytes.size + 1)
        body[0] = 0
        System.arraycopy(bytes, 0, body, 1, bytes.size)
        return derTag(0x03, body)
    }

    private fun derOid(arcs: IntArray): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(arcs[0] * 40 + arcs[1])
        for (i in 2 until arcs.size) {
            var v = arcs[i]
            val stack = IntArray(5)
            var count = 0
            stack[count++] = v and 0x7F
            v = v ushr 7
            while (v > 0) {
                stack[count++] = (v and 0x7F) or 0x80
                v = v ushr 7
            }
            for (j in count - 1 downTo 0) out.write(stack[j])
        }
        return derTag(0x06, out.toByteArray())
    }

    private fun derUtcOrGenTime(date: Date): ByteArray {
        val fmt = SimpleDateFormat("yyyyMMddHHmmss'Z'", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }
        val text = fmt.format(date)
        return if (text.substring(0, 4).toInt() < 2050) {
            derTag(0x17, text.substring(2).toByteArray(Charsets.US_ASCII)) // UTCTime
        } else {
            derTag(0x18, text.toByteArray(Charsets.US_ASCII)) // GeneralizedTime
        }
    }
}

/**
 * Copyable high-entropy one-time pairing invite per docs/protocol.md.
 * Contains protocol version, inviteId (16B), secret (>=128-bit), Android SPKI SHA-256 fingerprint,
 * and optional host/port hint.
 */
data class SrapPairingInvite(
    val major: Int = SrapCodec.MAJOR,
    val inviteId: ByteArray,
    val secret: ByteArray,
    val androidFingerprintSha256: String,
    val hostHint: String?,
    val portHint: Int,
    val expiresAtMs: Long
) {
    val inviteIdB64: String get() = SrapCrypto.base64UrlEncodeNoPad(inviteId)
    val secretB64: String get() = SrapCrypto.base64UrlEncodeNoPad(secret)

    override fun toString(): String = "SrapPairingInvite(redacted)"

    fun toCopyableText(): String {
        val sb = StringBuilder(160)
        sb.append("sonicroute://pair?v=").append(major)
        sb.append("&id=").append(inviteIdB64)
        sb.append("&s=").append(secretB64)
        sb.append("&fp=").append(androidFingerprintSha256)
        if (hostHint != null && validHost(hostHint, generating = true) && portHint in 1..65535) {
            sb.append("&h=").append(hostHint)
            sb.append("&p=").append(portHint)
        }
        return sb.toString()
    }

    companion object {
        private val FP_REGEX = Regex("^[0-9A-F]{64}$")
        private val FIELDS = setOf("v", "id", "s", "fp", "h", "p")

        fun create(
            androidFingerprintSha256: String,
            hostHint: String?,
            portHint: Int,
            nowMs: Long = System.currentTimeMillis(),
            ttlMs: Long = 120_000L
        ): SrapPairingInvite {
            val normalizedFp = androidFingerprintSha256.uppercase(Locale.ROOT)
            require(FP_REGEX.matches(normalizedFp)) { "Invalid Android SPKI SHA-256 fingerprint" }
            val host = hostHint?.takeIf { portHint in 1..65535 && validHost(it, generating = true) }
            return SrapPairingInvite(
                major = SrapCodec.MAJOR,
                inviteId = SrapCrypto.randomBytes(16),
                secret = SrapCrypto.randomBytes(16), // 128-bit high-entropy one-time secret
                androidFingerprintSha256 = normalizedFp,
                hostHint = host,
                portHint = if (host == null) 0 else portHint,
                expiresAtMs = nowMs + ttlMs
            )
        }

        fun parse(uriText: String): SrapPairingInvite {
            val trimmed = uriText.trim()
            if (trimmed.length > 1024 || !trimmed.startsWith("sonicroute://pair?")) {
                throw SrapProtocolException("Unsupported invite scheme")
            }
            val query = trimmed.substring("sonicroute://pair?".length)
            val params = LinkedHashMap<String, String>()
            for (part in query.split('&')) {
                val eq = part.indexOf('=')
                if (eq <= 0 || eq >= part.length - 1) throw SrapProtocolException("Malformed invite parameter")
                val k = part.substring(0, eq)
                val v = part.substring(eq + 1)
                if (k !in FIELDS || params.put(k, v) != null) throw SrapProtocolException("Invalid invite parameter")
            }
            if (params["v"] != "1") throw SrapProtocolException("Unsupported invite version")
            val inviteId = SrapCrypto.base64UrlDecodeNoPad(params["id"] ?: throw SrapProtocolException("Missing id"))
            if (inviteId.size != 16) throw SrapProtocolException("Invalid inviteId length")
            val secret = SrapCrypto.base64UrlDecodeNoPad(params["s"] ?: throw SrapProtocolException("Missing secret"))
            if (secret.size !in 16..32) throw SrapProtocolException("Invalid invite secret length")
            val fp = (params["fp"] ?: throw SrapProtocolException("Missing fp")).uppercase(Locale.ROOT)
            if (!FP_REGEX.matches(fp)) throw SrapProtocolException("Invalid fingerprint format")
            val host = params["h"]
            val portText = params["p"]
            if ((host == null) != (portText == null) || host != null && !validHost(host, generating = false)) {
                throw SrapProtocolException("Invalid invite address")
            }
            val port = portText?.toIntOrNull() ?: 0
            if (portText != null && (port !in 1..65535 || port.toString() != portText)) throw SrapProtocolException("Invalid invite port")
            return SrapPairingInvite(SrapCodec.MAJOR, inviteId, secret, fp, host, port, 0L)
        }

        private fun validHost(host: String, generating: Boolean): Boolean {
            if (host.isEmpty() || host.length > 60) return false
            if (':' !in host) {
                val parts = host.split('.')
                if (parts.size != 4 || parts.any { part ->
                    val n = part.toIntOrNull()
                    n == null || n !in 0..255 || part != n.toString()
                }) return false
                val first = parts.first().toInt()
                return first in 1..223 && first != 127
            }
            val raw = if (host.startsWith('[') && host.endsWith(']')) host.substring(1, host.lastIndex) else host
            val literal = raw.substringBefore('%')
            if (literal.isEmpty() || literal.length > 45 || literal.any {
                it !in '0'..'9' && it !in 'A'..'F' && it !in 'a'..'f' && it != ':' && it != '.'
            }) return false
            val hasScope = literal.length != raw.length
            if (hasScope) {
                val suffix = raw.substring(literal.length)
                if (!suffix.startsWith("%25")) return false
                val index = suffix.substring(3)
                if (index.isEmpty() || index.any { it !in '0'..'9' } || index.toUIntOrNull()?.let { it > 0u } != true) return false
            }
            // A colon and only IPv6 literal characters have been checked above. No DNS name reaches this call.
            val address = runCatching { InetAddress.getByName(literal) }.getOrNull() as? Inet6Address ?: return false
            return !address.isAnyLocalAddress && !address.isLoopbackAddress && !address.isMulticastAddress &&
                (!address.isLinkLocalAddress || !generating && hasScope) && (!generating || !hasScope)
        }
    }
}
