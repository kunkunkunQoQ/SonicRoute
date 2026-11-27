package org.sonicroute.platform;

import android.os.Bundle;

/**
 * Strictly whitelisted local feasibility probe executed inside an official Shizuku UserService.
 * Never executes arbitrary shell commands or generic Binder transactions, and never mutates
 * third-party applications' audio state.
 */
interface IPrivilegedAudioProbe {
    void destroy() = 16777114;
    Bundle probeAudioCapabilities(int callerAppUid, int selfAudioSessionId) = 1;
}
