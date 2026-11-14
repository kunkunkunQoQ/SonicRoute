// SPDX-License-Identifier: MIT
package org.sonicroute.android;

import android.os.IBinder;
import java.io.File;
import java.lang.reflect.Array;
import java.util.Map;

/**
 * Standalone MIT wireless ADB host unpairing helper eliminating runtime dependence on third-party server binaries.
 * Preserves the {@code SR_UNPAIR_NOT_PAIRED} / {@code SR_UNPAIR_REQUESTED} stdout contract expected by
 * {@code AndroidWirelessConnection.UnpairAsync}.
 */
public final class PairingControl {
    private static final String FINGERPRINT_PATTERN = "(?i)[0-9a-f]{2}(:[0-9a-f]{2}){15}";
    private static final String TEMP_PAIRING_JAR_PATTERN = "/data/local/tmp/sonicroute-pairing-[0-9a-f]{32}\\.jar";
    private static final String SHARED_CATALOG_JAR = "/data/local/tmp/sonicroute-catalog.jar";
    private static final String KEEP_MARKER = "--keep";

    private PairingControl() {
    }

    public static void main(String[] args) throws Exception {
        if (args.length < 1 || args.length > 2 || args[0] == null || !args[0].matches(FINGERPRINT_PATTERN)) {
            throw new IllegalArgumentException("Invalid pairing request");
        }

        File tempHelperToDelete = null;
        if (args.length == 2) {
            String second = args[1];
            if (second != null && second.matches(TEMP_PAIRING_JAR_PATTERN)) {
                tempHelperToDelete = new File(second);
            } else if (!KEEP_MARKER.equals(second) && !SHARED_CATALOG_JAR.equals(second)) {
                throw new IllegalArgumentException("Invalid pairing helper path");
            }
        }

        IBinder binder = (IBinder) Class.forName("android.os.ServiceManager")
                .getMethod("getService", String.class)
                .invoke(null, "adb");
        Class<?> api = Class.forName("android.debug.IAdbManager");
        Object service = Class.forName("android.debug.IAdbManager$Stub")
                .getMethod("asInterface", IBinder.class)
                .invoke(null, binder);

        boolean matched = isFingerprintPaired(args[0], api.getMethod("getPairedDevices").invoke(service));

        // DEX is already loaded in memory; delete one-shot helper jars before unpair tears down ADB.
        if (tempHelperToDelete != null) {
            tempHelperToDelete.delete();
        }

        if (!matched) {
            System.out.println("SR_UNPAIR_NOT_PAIRED");
            System.out.flush();
            return;
        }

        api.getMethod("unpairDevice", String.class).invoke(service, args[0]);
        System.out.println("SR_UNPAIR_REQUESTED");
        System.out.flush();
    }

    private static boolean isFingerprintPaired(String targetFingerprint, Object pairs) throws ReflectiveOperationException {
        if (pairs == null) {
            return false;
        }
        if (pairs instanceof Map<?, ?>) {
            for (Object key : ((Map<?, ?>) pairs).keySet()) {
                if (key instanceof String && targetFingerprint.equalsIgnoreCase((String) key)) {
                    return true;
                }
            }
            return false;
        }
        if (pairs.getClass().isArray()) {
            int length = Array.getLength(pairs);
            for (int i = 0; i < length && i < 128; ++i) {
                Object pair = Array.get(pairs, i);
                if (pair == null) {
                    continue;
                }
                String fingerprint = (String) pair.getClass().getField("keyFingerprint").get(pair);
                if (targetFingerprint.equalsIgnoreCase(fingerprint)) {
                    return true;
                }
            }
        }
        return false;
    }
}
