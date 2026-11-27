package org.sonicroute.app.test

import android.app.Instrumentation
import android.graphics.Bitmap
import android.graphics.Color
import android.os.Bundle
import android.os.SystemClock
import android.provider.Settings
import java.io.File
import kotlin.math.abs

internal class PhoneUiEvidence(private val instrument: Instrumentation, token: String) {
    private val folder: File
    init {
        require(token.matches(Regex("[a-f0-9]{32}")))
        folder = File(instrument.targetContext.cacheDir, "ui-$token").also { check(it.mkdir()) }
    }

    fun save(name: String) {
        require(name.matches(Regex("[a-z][a-z0-9_-]{0,60}")))
        instrument.waitForIdleSync()
        val root = requireNotNull(instrument.uiAutomation.rootInActiveWindow)
        check(root.packageName == "org.sonicroute.app" && root.findAccessibilityNodeInfosByText("sonicroute://").isEmpty())
        val image = stableImage()
        val file = File(folder, "$name.png")
        try { file.outputStream().use { check(image.compress(Bitmap.CompressFormat.PNG, 100, it)) } }
        finally { image.recycle() }
        instrument.sendStatus(1, Bundle().apply { putString("image", file.name) })
    }

    private fun stableImage(): Bitmap {
        val resolver = instrument.targetContext.contentResolver
        val scale = maxOf(Settings.Global.getFloat(resolver, Settings.Global.WINDOW_ANIMATION_SCALE, 1f),
            Settings.Global.getFloat(resolver, Settings.Global.TRANSITION_ANIMATION_SCALE, 1f))
        SystemClock.sleep((450 * scale.coerceIn(0f, 10f)).toLong() + 100)
        var previous: IntArray? = null
        repeat(8) {
            val raw = requireNotNull(instrument.uiAutomation.takeScreenshot())
            val frame = if (raw.config == Bitmap.Config.HARDWARE) raw.copy(Bitmap.Config.ARGB_8888, false).also { raw.recycle() } else raw
            val sample = IntArray(336) { index ->
                frame.getPixel(frame.width * (index % 16 + 1) / 17, frame.height * (index / 16 + 2) / 24)
            }
            val old = previous
            if (old != null && sample.indices.count { i ->
                    abs(Color.red(old[i]) - Color.red(sample[i])) + abs(Color.green(old[i]) - Color.green(sample[i])) +
                        abs(Color.blue(old[i]) - Color.blue(sample[i])) > 12
                } <= 2) return frame
            frame.recycle()
            previous = sample
            SystemClock.sleep(140)
        }
        error("visible_window_did_not_settle")
    }
}
