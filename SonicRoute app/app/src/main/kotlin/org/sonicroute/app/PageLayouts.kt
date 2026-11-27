package org.sonicroute.app

import android.content.Context
import android.util.AttributeSet
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import kotlin.math.min

internal class PageColumn(context: Context, attrs: AttributeSet) : LinearLayout(context, attrs) {
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = min(MeasureSpec.getSize(widthMeasureSpec), (680 * resources.displayMetrics.density).toInt())
        super.onMeasure(MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY), heightMeasureSpec)
    }
}

/** Native buttons keep their labels at large font scales and wrap onto the next line. */
internal class ActionStrip(context: Context, attrs: AttributeSet? = null) : ViewGroup(context, attrs) {
    private val gap = (6 * resources.displayMetrics.density).toInt()

    override fun generateDefaultLayoutParams() = LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT)

    private fun rows(width: Int, layout: Boolean): Int {
        var x = 0
        var y = paddingTop
        var line = 0
        val available = (width - paddingLeft - paddingRight).coerceAtLeast(0)
        for (i in 0 until childCount) {
            val child = getChildAt(i)
            if (child.visibility == View.GONE) continue
            if (!layout) child.measure(MeasureSpec.makeMeasureSpec(available, MeasureSpec.AT_MOST),
                MeasureSpec.makeMeasureSpec(0, MeasureSpec.UNSPECIFIED))
            if (x > 0 && x + child.measuredWidth > available) { x = 0; y += line + gap; line = 0 }
            if (layout) {
                val left = if (layoutDirection == View.LAYOUT_DIRECTION_RTL) width - paddingRight - x - child.measuredWidth else paddingLeft + x
                child.layout(left, y, left + child.measuredWidth, y + child.measuredHeight)
            }
            x += child.measuredWidth + gap
            line = maxOf(line, child.measuredHeight)
        }
        return y + line + paddingBottom
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        setMeasuredDimension(width, resolveSize(rows(width, false), heightMeasureSpec))
    }

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) { rows(r - l, true) }
}

internal fun Context.rowButton(style: Int = R.style.SonicRoute_BackendButton) = Button(this, null, 0, style)
