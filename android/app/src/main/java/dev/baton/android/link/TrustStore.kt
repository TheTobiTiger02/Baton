package dev.baton.android.link

import android.content.Context
import android.os.Build
import android.provider.Settings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** The paired PC. [trustKey] never leaves the phone after pairing; it only keys the session proof. */
data class TrustedHost(
    val hostId: String,
    val pcName: String,
    val certificateFingerprint: String,
    val trustKey: String
)

/**
 * This phone's identity and its paired PC. The trust key is encrypted with an Android Keystore
 * AES-GCM key, so a copied preferences file is useless on another device.
 */
class TrustStore(context: Context) {
    private val appContext = context.applicationContext
    private val preferences = appContext.getSharedPreferences("baton_link", Context.MODE_PRIVATE)

    val deviceId: String
        get() = preferences.getString(KEY_DEVICE_ID, null)?.takeIf { it.isNotBlank() }
            ?: "android-${UUID.randomUUID().toString().replace("-", "")}".also {
                preferences.edit().putString(KEY_DEVICE_ID, it).apply()
            }

    /** The name the PC shows: the user's device name when set, else the model. */
    val displayName: String
        get() = runCatching { Settings.Global.getString(appContext.contentResolver, Settings.Global.DEVICE_NAME) }
            .getOrNull()?.takeIf { it.isNotBlank() } ?: model

    val model: String
        get() = "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}".trim()

    val host: TrustedHost?
        get() {
            val hostId = preferences.getString(KEY_HOST_ID, null) ?: return null
            val encrypted = preferences.getString(KEY_TRUST_KEY, null) ?: return null
            val iv = preferences.getString(KEY_TRUST_KEY_IV, null) ?: return null
            val trustKey = decrypt(encrypted, iv) ?: return null
            return TrustedHost(
                hostId = hostId,
                pcName = preferences.getString(KEY_PC_NAME, null) ?: "PC",
                certificateFingerprint = preferences.getString(KEY_FINGERPRINT, null) ?: return null,
                trustKey = trustKey
            )
        }

    val isPaired: Boolean get() = host != null

    fun save(host: TrustedHost): Boolean {
        val (encrypted, iv) = encrypt(host.trustKey) ?: return false
        return preferences.edit()
            .putString(KEY_HOST_ID, host.hostId)
            .putString(KEY_PC_NAME, host.pcName)
            .putString(KEY_FINGERPRINT, host.certificateFingerprint)
            .putString(KEY_TRUST_KEY, encrypted)
            .putString(KEY_TRUST_KEY_IV, iv)
            .commit()
    }

    fun updatePcName(name: String) {
        if (name.isNotBlank()) preferences.edit().putString(KEY_PC_NAME, name).apply()
    }

    fun forget() {
        preferences.edit()
            .remove(KEY_HOST_ID)
            .remove(KEY_PC_NAME)
            .remove(KEY_FINGERPRINT)
            .remove(KEY_TRUST_KEY)
            .remove(KEY_TRUST_KEY_IV)
            .commit()
        EndpointStore(appContext).clear()
    }

    private fun encrypt(value: String): Pair<String, String>? = runCatching {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())
        Base64.encodeToString(cipher.doFinal(value.toByteArray(Charsets.UTF_8)), Base64.NO_WRAP) to
            Base64.encodeToString(cipher.iv, Base64.NO_WRAP)
    }.getOrNull()

    private fun decrypt(encrypted: String, iv: String): String? = runCatching {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, Base64.decode(iv, Base64.NO_WRAP)))
        String(cipher.doFinal(Base64.decode(encrypted, Base64.NO_WRAP)), Charsets.UTF_8)
    }.getOrNull()

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(KEYSTORE_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(KEYSTORE_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setRandomizedEncryptionRequired(true)
                    .build()
            )
            generateKey()
        }
    }

    companion object {
        private const val KEY_DEVICE_ID = "device_id"
        private const val KEY_HOST_ID = "host_id"
        private const val KEY_PC_NAME = "pc_name"
        private const val KEY_FINGERPRINT = "certificate_fingerprint"
        private const val KEY_TRUST_KEY = "trust_key"
        private const val KEY_TRUST_KEY_IV = "trust_key_iv"
        private const val KEYSTORE_ALIAS = "baton_trusted_host_v1"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}
