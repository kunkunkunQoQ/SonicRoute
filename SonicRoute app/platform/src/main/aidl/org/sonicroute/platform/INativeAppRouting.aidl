package org.sonicroute.platform;
import org.sonicroute.platform.INativeAppRouteObserver;

interface INativeAppRouting {
    void destroy() = 16777114;
    oneway void applyRoute(String packageName, int expectedAppUid, String expectedSigner, int targetDeviceId, int expectedType, String expectedAddress, INativeAppRouteObserver observer) = 1;
    oneway void restoreRoute(INativeAppRouteObserver observer) = 2;
}
