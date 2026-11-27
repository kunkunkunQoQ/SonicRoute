package org.sonicroute.app.test

import android.app.Instrumentation
import android.os.SystemClock
import android.view.accessibility.AccessibilityNodeInfo

/** Only selects exact entries in SonicRoute-owned dialogs. System grants remain with the user. */
internal class CaptureDialogInterop(private val instrument: Instrumentation) {
    fun choosePeerAndSource(pin: String, source: String) {
        require(source in setOf("bin.mt.plus", "cn.wenyu.bodian"))
        check(select(pin, false) { it == "SonicRoute Interop\n$pin" })
        check(select(source, true) { it.substringAfterLast('\n') == source })
    }

    private fun select(key: String, scroll: Boolean, matches: (String) -> Boolean): Boolean {
        val automation = instrument.uiAutomation
        val end = SystemClock.elapsedRealtime() + 14000
        do {
            val root = automation.rootInActiveWindow
            if (root != null && root.packageName == instrument.targetContext.packageName) {
                var node = root.findAccessibilityNodeInfosByText(key).singleOrNull { matches(it.text?.toString().orEmpty()) }
                if (node != null) {
                    while (node != null) {
                        if (node.isClickable && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) return true
                        node = node.parent
                    }
                } else if (scroll) {
                    findScrollable(root)?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD)
                }
            }
            SystemClock.sleep(180)
        } while (SystemClock.elapsedRealtime() < end)
        return false
    }

    private fun findScrollable(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
        if (node.isScrollable) return node
        for (index in 0 until node.childCount) {
            val child = node.getChild(index) ?: continue
            findScrollable(child)?.let { return it }
        }
        return null
    }
}
