package org.sonicroute.platform

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.sonicroute.core.SrapServerIdentity
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.security.auth.x500.X500Principal

/**
 * Hardware-backed Android Keystore EC P-256 installation identity for SRAP v1.0 TLS and pairing.
 * Private keys stay inside AndroidKeyStore and are never exported or logged.
 */
object AndroidKeystoreIdentity {
    private const val ANDROID_KEYSTORE = "AndroidKeyStore"
    private const val KEY_ALIAS = "sonicroute_srap_identity_v1"
    private const val VALIDITY_MS = 10L * 365L * 24L * 60L * 60L * 1000L

    @Synchronized
    fun getOrCreate(): SrapServerIdentity {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        val existingKey = runCatching { ks.getKey(KEY_ALIAS, null) as? PrivateKey }.getOrNull()
        val existingCert = runCatching { ks.getCertificate(KEY_ALIAS) as? X509Certificate }.getOrNull()
        if (existingKey != null && existingCert != null && existingCert.publicKey.algorithm == "EC") {
            return SrapServerIdentity(existingKey, existingCert)
        }
        return generateFresh(ks)
    }

    @Synchronized
    fun rotate(): SrapServerIdentity {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        runCatching { if (ks.containsAlias(KEY_ALIAS)) ks.deleteEntry(KEY_ALIAS) }
        return generateFresh(ks)
    }

    private fun generateFresh(ks: KeyStore): SrapServerIdentity {
        runCatching { if (ks.containsAlias(KEY_ALIAS)) ks.deleteEntry(KEY_ALIAS) }
        val now = Date(System.currentTimeMillis() - 60_000L)
        val notAfter = Date(now.time + VALIDITY_MS)
        val spec = KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(
                KeyProperties.DIGEST_SHA256,
                KeyProperties.DIGEST_SHA384,
                KeyProperties.DIGEST_NONE
            )
            .setCertificateSubject(X500Principal("CN=SonicRoute Android"))
            .setCertificateSerialNumber(BigInteger.ONE)
            .setCertificateNotBefore(now)
            .setCertificateNotAfter(notAfter)
            .build()

        val kpg = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEYSTORE)
        kpg.initialize(spec)
        kpg.generateKeyPair()

        val privateKey = ks.getKey(KEY_ALIAS, null) as? PrivateKey
            ?: throw IllegalStateException("AndroidKeyStore failed to return EC private key")
        val cert = ks.getCertificate(KEY_ALIAS) as? X509Certificate
            ?: throw IllegalStateException("AndroidKeyStore failed to return X.509 certificate")
        return SrapServerIdentity(privateKey, cert)
    }
}
