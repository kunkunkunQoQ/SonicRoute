package org.sonicroute.app.test

import android.app.Activity
import android.app.Application
import android.app.Instrumentation
import android.os.Bundle
import android.os.SystemClock
import org.sonicroute.app.MainActivity
import java.util.concurrent.FutureTask

internal fun <T> Instrumentation.onMain(action: () -> T): T = FutureTask(action).let { runOnMainSync(it); it.get() }

/** The host brings the app to the foreground after launch=ready. */
internal class InteropActivity(private val instrument: Instrumentation) : AutoCloseable {
    private val application = instrument.targetContext.applicationContext as Application
    private val lifecycle = java.lang.Object()
    @Volatile var current: MainActivity? = null
        private set
    private val callbacks = object : Application.ActivityLifecycleCallbacks {
        override fun onActivityResumed(a: Activity) {
            if (a is MainActivity) synchronized(lifecycle) { current = a; lifecycle.notifyAll() }
        }
        override fun onActivityPaused(a: Activity) {
            synchronized(lifecycle) { if (a === current) current = null }
        }
        override fun onActivityCreated(a: Activity, b: Bundle?) = Unit
        override fun onActivityStarted(a: Activity) = Unit
        override fun onActivityStopped(a: Activity) = Unit
        override fun onActivitySaveInstanceState(a: Activity, b: Bundle) = Unit
        override fun onActivityDestroyed(a: Activity) = Unit
    }

    fun launch(): MainActivity {
        application.registerActivityLifecycleCallbacks(callbacks)
        val monitor = instrument.addMonitor(MainActivity::class.java.name, null, false)
        try {
            instrument.sendStatus(1, Bundle().apply { putString("launch", "ready") })
            check(instrument.waitForMonitorWithTimeout(monitor, 25000) is MainActivity || current != null) {
                "host_activity_launch_timeout"
            }
        } finally { instrument.removeMonitor(monitor) }
        return awaitResumed(null)
    }

    fun recreate(page: MainActivity): MainActivity {
        instrument.onMain { page.recreate() }
        return awaitResumed(page)
    }

    private fun awaitResumed(previous: MainActivity?): MainActivity {
        val deadline = SystemClock.elapsedRealtime() + 12000
        synchronized(lifecycle) {
            while (true) {
                current?.takeUnless { it === previous }?.let { return it }
                val remaining = deadline - SystemClock.elapsedRealtime()
                check(remaining > 0) { "activity_resume_timeout" }
                lifecycle.wait(remaining)
            }
        }
    }

    override fun close() {
        instrument.onMain {
            current?.finish()
            application.unregisterActivityLifecycleCallbacks(callbacks)
        }
    }
}
