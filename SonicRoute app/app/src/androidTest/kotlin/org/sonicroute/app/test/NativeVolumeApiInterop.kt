package org.sonicroute.app.test

import android.app.Activity
import android.app.Instrumentation
import android.media.AudioManager
import android.media.AudioTrack
import android.media.MediaPlayer
import android.media.VolumeShaper
import android.os.Build
import android.os.Bundle
import java.lang.reflect.Method
import java.lang.reflect.Modifier
import java.util.Locale

/** Test APK only. Examines visible ROM method declarations without obtaining or changing a player. */
internal class NativeVolumeApiInterop(private val instrument: Instrumentation) {
    fun run() {
        val results = Bundle()
        try {
            fun signature(method: Method) = method.name + method.parameterTypes.joinToString(",", "(", ")") { it.simpleName } +
                "->" + method.returnType.simpleName
            fun visibleMethods(type: Class<*>) = type.methods.filter { Modifier.isPublic(it.modifiers) }
            val attributes = listOf("appvolume", "uidvolume", "appmute", "applicationvolume", "volumeforuid")
            val hints = visibleMethods(AudioManager::class.java).filter { method ->
                attributes.any { it in method.name.lowercase(Locale.ROOT) }
            }.map(::signature).distinct().sorted()
            val proxy = runCatching { Class.forName("android.media.PlayerProxy", false, AudioManager::class.java.classLoader) }.getOrNull()
            val methods = proxy?.let { runCatching { visibleMethods(it) }.getOrNull() }
            val shapers = methods?.filter { it.name.contains("VolumeShaper") }?.map(::signature)?.distinct()?.sorted()
            results.putString("device", "${Build.MANUFACTURER}/${Build.MODEL}/API${Build.VERSION.SDK_INT}")
            results.putString("scope", "standard_app_visible_public_declarations")
            results.putString("oemVolumeMethods", hints.take(16).joinToString("|").ifEmpty { "none_visible" })
            results.putString("playerProxyShaper", shapers?.take(12)?.joinToString("|")?.ifEmpty { "none_visible" } ?: "class_unavailable")
            val owned = listOf(MediaPlayer::class.java, AudioTrack::class.java).all {
                it.getMethod("createVolumeShaper", VolumeShaper.Configuration::class.java).returnType == VolumeShaper::class.java
            } && VolumeShaper::class.java.getMethod("getVolume").returnType == Float::class.javaPrimitiveType &&
                VolumeShaper::class.java.getMethod("close").returnType == Void.TYPE
            results.putString("ownedPlayerShaper", owned.toString())
            results.putString("interop", "passed")
        } catch (failure: Exception) {
            results.putString("interop", "failed-${failure.javaClass.simpleName}")
        }
        instrument.finish(if (results.getString("interop") == "passed") Activity.RESULT_OK else Activity.RESULT_CANCELED, results)
    }
}
