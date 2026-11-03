/* Revoke only this host's wireless pairing, never all debugging keys. SPDX-License-Identifier: Apache-2.0 */
package com.genymobile.scrcpy.control;

import android.os.IBinder;
import java.io.File;
import java.lang.reflect.Array;

public final class PairingControl {
    private PairingControl() { }
    public static void main(String[] args) throws Exception {
        if (args.length != 2 || !args[0].matches("(?i)[0-9a-f]{2}(:[0-9a-f]{2}){15}")
                || !args[1].matches("/data/local/tmp/sonicroute-pairing-[0-9a-f]{32}\\.jar")) throw new IllegalArgumentException("Invalid pairing request");
        IBinder binder = (IBinder) Class.forName("android.os.ServiceManager").getMethod("getService", String.class).invoke(null, "adb");
        Class<?> api = Class.forName("android.debug.IAdbManager");
        Object service = Class.forName("android.debug.IAdbManager$Stub").getMethod("asInterface", IBinder.class).invoke(null, binder);
        Object pairs = api.getMethod("getPairedDevices").invoke(service);
        boolean matched = false;
        for (int i = 0; pairs != null && i < Array.getLength(pairs) && i < 128; ++i) {
            Object pair = Array.get(pairs, i);
            String fingerprint = (String) pair.getClass().getField("keyFingerprint").get(pair);
            if (args[0].equalsIgnoreCase(fingerprint)) { matched = true; break; }
        }
        // DEX is already loaded. Remove this one-shot helper before revocation can close its transport.
        new File(args[1]).delete();
        if (!matched) { System.out.println("SR_UNPAIR_NOT_PAIRED"); return; }
        api.getMethod("unpairDevice", String.class).invoke(service, args[0]);
        System.out.println("SR_UNPAIR_REQUESTED"); System.out.flush();
    }
}
