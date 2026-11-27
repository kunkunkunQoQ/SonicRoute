package org.sonicroute.platform;

oneway interface INativeAppRouteObserver {
    void routeState(String phase, String reason, in int[] originalDeviceIds, in int[] actualDeviceIds);
}
