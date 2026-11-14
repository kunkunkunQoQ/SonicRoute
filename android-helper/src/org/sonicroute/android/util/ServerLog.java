// SPDX-License-Identifier: MIT
package org.sonicroute.android.util;

import android.util.Log;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.OutputStream;
import java.io.PrintStream;
import java.util.Locale;

/**
 * Dual logger emitting formatted server diagnostics to stdout/stderr (with the
 * {@code [server] LEVEL: ...} prefix expected by host transports) and logcat.
 */
public final class ServerLog {
    private static final String LOG_TAG = "SonicRoute";
    private static final String STREAM_PREFIX = "[server] ";

    private static final PrintStream STDOUT = new PrintStream(new FileOutputStream(FileDescriptor.out), true);
    private static final PrintStream STDERR = new PrintStream(new FileOutputStream(FileDescriptor.err), true);

    public enum Level {
        VERBOSE,
        DEBUG,
        INFO,
        WARN,
        ERROR;

        public static Level parse(String raw) {
            if (raw == null || raw.isEmpty()) {
                throw new IllegalArgumentException("Empty log_level");
            }
            return Level.valueOf(raw.trim().toUpperCase(Locale.ENGLISH));
        }
    }

    private static volatile Level minLevel = Level.INFO;

    private ServerLog() {
    }

    public static void setLevel(Level level) {
        if (level != null) {
            minLevel = level;
        }
    }

    public static boolean isEnabled(Level level) {
        return level.ordinal() >= minLevel.ordinal();
    }

    /**
     * Suppresses {@code System.out}/{@code System.err} after Android framework logging takes
     * over, while keeping {@link ServerLog} console streams intact for the host transport.
     */
    public static void silenceStandardStreams() {
        PrintStream sink = new PrintStream(new OutputStream() {
            @Override
            public void write(int b) {
            }

            @Override
            public void write(byte[] b, int off, int len) {
            }
        });
        System.setOut(sink);
        System.setErr(sink);
    }

    public static void v(String message) {
        if (!isEnabled(Level.VERBOSE)) {
            return;
        }
        tryLogcat(Log.VERBOSE, message, null);
        synchronized (STDOUT) {
            STDOUT.println(STREAM_PREFIX + "VERBOSE: " + message);
        }
    }

    public static void d(String message) {
        if (!isEnabled(Level.DEBUG)) {
            return;
        }
        tryLogcat(Log.DEBUG, message, null);
        synchronized (STDOUT) {
            STDOUT.println(STREAM_PREFIX + "DEBUG: " + message);
        }
    }

    public static void i(String message) {
        if (!isEnabled(Level.INFO)) {
            return;
        }
        tryLogcat(Log.INFO, message, null);
        synchronized (STDOUT) {
            STDOUT.println(STREAM_PREFIX + "INFO: " + message);
        }
    }

    public static void w(String message) {
        w(message, null);
    }

    public static void w(String message, Throwable error) {
        if (!isEnabled(Level.WARN)) {
            return;
        }
        tryLogcat(Log.WARN, message, error);
        synchronized (STDERR) {
            STDERR.println(STREAM_PREFIX + "WARN: " + message);
            if (error != null) {
                error.printStackTrace(STDERR);
            }
        }
    }

    public static void e(String message) {
        e(message, null);
    }

    public static void e(String message, Throwable error) {
        if (!isEnabled(Level.ERROR)) {
            return;
        }
        tryLogcat(Log.ERROR, message, error);
        synchronized (STDERR) {
            STDERR.println(STREAM_PREFIX + "ERROR: " + message);
            if (error != null) {
                error.printStackTrace(STDERR);
            }
        }
    }

    private static void tryLogcat(int priority, String message, Throwable error) {
        try {
            if (error != null) {
                Log.println(priority, LOG_TAG, message + '\n' + Log.getStackTraceString(error));
            } else {
                Log.println(priority, LOG_TAG, message);
            }
        } catch (Throwable ignored) {
            // Host-side JVM unit tests run without Android liblog.
        }
    }
}
