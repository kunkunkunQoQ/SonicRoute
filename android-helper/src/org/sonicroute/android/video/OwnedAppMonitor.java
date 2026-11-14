// SPDX-License-Identifier: MIT
package org.sonicroute.android.video;

import android.app.KeyguardManager;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import java.io.Closeable;
import java.io.IOException;
import java.util.List;
import org.sonicroute.android.ShellContext;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.platform.AndroidSystemPlatform.TaskSnapshot;
import org.sonicroute.android.util.ServerLog;

/**
 * Event-driven monitor for an app pinned to a SonicRoute virtual display.
 * Uses {@code ITaskStackListener} Binder callbacks coalesced over a 100 ms handler window
 * with zero background polling.
 */
public final class OwnedAppMonitor implements Closeable {
    public static final String STATE_ACTIVE = "active";
    public static final String STATE_PHONE = "phone";
    public static final String STATE_RECOVERING = "recovering";
    public static final String STATE_MISSING = "missing";
    public static final String STATE_UNKNOWN = "unknown";

    private static final long COALESCE_MS = 100L;
    private static final long LAUNCH_GRACE_MS = 2500L;

    private final int displayId;
    private final Handler handler;
    private final Runnable relaunchAction;
    private final Runnable evaluateRunnable = this::evaluateNow;
    private final Runnable verifyLaunchRunnable = this::onLaunchGraceExpired;

    private Closeable taskListenerHandle;
    private String ownedPackage;
    private String lastReportedState;
    private boolean everActive;
    private boolean launching;
    private boolean recoverAttempted;
    private boolean closed;

    public OwnedAppMonitor(int displayId, Runnable relaunchAction) {
        this.displayId = displayId;
        this.handler = new Handler(Looper.getMainLooper());
        this.relaunchAction = relaunchAction;
        this.taskListenerHandle = AndroidSystemPlatform.registerTaskStackListener(this::scheduleEvaluate);
    }

    public synchronized String getOwnedPackage() {
        return ownedPackage;
    }

    public void setOwnedPackage(String packageSpec) {
        String clean = stripLaunchPrefix(packageSpec);
        synchronized (this) {
            if (closed || clean == null || clean.isEmpty()) {
                return;
            }
            if (!clean.equals(ownedPackage)) {
                ownedPackage = clean;
                lastReportedState = null;
                everActive = false;
                recoverAttempted = false;
            }
        }
        notifyLaunching();
    }

    /**
     * Arms a short startup grace window so async app launch transitions do not emit a false
     * {@code missing} state before the task renders on the virtual display.
     */
    public void notifyLaunching() {
        synchronized (this) {
            if (closed || ownedPackage == null) {
                return;
            }
            launching = true;
            if (everActive || lastReportedState != null) {
                emitStateLocked(STATE_RECOVERING);
            }
        }
        handler.removeCallbacks(verifyLaunchRunnable);
        handler.postDelayed(verifyLaunchRunnable, LAUNCH_GRACE_MS);
        scheduleEvaluate();
    }

    private void onLaunchGraceExpired() {
        synchronized (this) {
            if (closed) {
                return;
            }
            launching = false;
        }
        evaluateNow();
    }

    public void scheduleEvaluate() {
        synchronized (this) {
            if (closed || ownedPackage == null) {
                return;
            }
        }
        handler.removeCallbacks(evaluateRunnable);
        handler.postDelayed(evaluateRunnable, COALESCE_MS);
    }

    private void evaluateNow() {
        final String pkg;
        synchronized (this) {
            if (closed || ownedPackage == null || displayId <= 0) {
                return;
            }
            pkg = ownedPackage;
        }

        List<TaskSnapshot> tasks = AndroidSystemPlatform.queryTasks();
        String nextState = classifyState(tasks, displayId, pkg);

        boolean shouldRecover = false;
        synchronized (this) {
            if (closed) {
                return;
            }
            if (STATE_ACTIVE.equals(nextState)) {
                launching = false;
                handler.removeCallbacks(verifyLaunchRunnable);
                everActive = true;
                recoverAttempted = false;
                emitStateLocked(STATE_ACTIVE);
            } else if (!launching) {
                if (STATE_PHONE.equals(nextState)) {
                    emitStateLocked(STATE_PHONE);
                } else if (everActive && !recoverAttempted && !isDeviceLocked()) {
                    recoverAttempted = true;
                    launching = true;
                    shouldRecover = true;
                    emitStateLocked(STATE_RECOVERING);
                    handler.removeCallbacks(verifyLaunchRunnable);
                    handler.postDelayed(verifyLaunchRunnable, LAUNCH_GRACE_MS);
                } else {
                    emitStateLocked(nextState);
                }
            }
        }

        if (shouldRecover && relaunchAction != null) {
            ServerLog.i("SonicRoute task recovery: display=" + displayId + " pkg=" + pkg);
            relaunchAction.run();
        }
    }

    public static String classifyState(List<TaskSnapshot> tasks, int virtualDisplayId, String targetPackage) {
        if (tasks == null) {
            return STATE_UNKNOWN;
        }
        boolean foundOnVirtual = false;
        boolean foundVisibleOnOther = false;
        for (TaskSnapshot task : tasks) {
            if (task == null || !task.matchesPackage(targetPackage)) {
                continue;
            }
            if (task.displayId == virtualDisplayId) {
                foundOnVirtual = true;
                break;
            }
            if (task.displayId != virtualDisplayId && task.visible) {
                foundVisibleOnOther = true;
            }
        }
        if (foundOnVirtual) {
            return STATE_ACTIVE;
        }
        if (foundVisibleOnOther) {
            return STATE_PHONE;
        }
        return STATE_MISSING;
    }

    private void emitStateLocked(String state) {
        if (!state.equals(lastReportedState)) {
            lastReportedState = state;
            ServerLog.i("SonicRoute app-state: display=" + displayId + " state=" + state);
        }
    }

    private static boolean isDeviceLocked() {
        try {
            KeyguardManager km = (KeyguardManager) ShellContext.get().getSystemService(Context.KEYGUARD_SERVICE);
            return km != null && km.isKeyguardLocked();
        } catch (Throwable ignored) {
            return false;
        }
    }

    public static String stripLaunchPrefix(String packageSpec) {
        if (packageSpec == null) {
            return null;
        }
        if (packageSpec.startsWith("+") || packageSpec.startsWith("?")) {
            return packageSpec.substring(1);
        }
        return packageSpec;
    }

    @Override
    public void close() {
        Closeable handle;
        synchronized (this) {
            if (closed) {
                return;
            }
            closed = true;
            handle = taskListenerHandle;
            taskListenerHandle = null;
        }
        handler.removeCallbacks(evaluateRunnable);
        handler.removeCallbacks(verifyLaunchRunnable);
        if (handle != null) {
            try {
                handle.close();
            } catch (IOException ignored) {
            }
        }
    }
}
