/* Phone mirror window geometry. Event driven; never changes the Android display.
 * SPDX-License-Identifier: Apache-2.0 */
#ifndef SONICROUTE_MIRROR_RESIZE_H
#define SONICROUTE_MIRROR_RESIZE_H
#include <stdint.h>
static bool sr_mirror_enabled(void) {
    static int enabled = -1;
    if (enabled == -1) {
        const char *value = SDL_getenv("SONICROUTE_PHONE_MIRROR");
        enabled = value && value[0] == '1';
    }
    return enabled != 0;
}
#ifdef _WIN32
static void sr_mirror_frame(HWND hwnd, LONG *width, LONG *height) {
    RECT outer, client;
    GetWindowRect(hwnd, &outer); GetClientRect(hwnd, &client);
    *width = outer.right - outer.left - client.right;
    *height = outer.bottom - outer.top - client.bottom;
}
static bool sr_mirror_fit(struct sc_screen *screen, LONG *width, LONG *height) {
    LONG cw = screen->content_size.width, ch = screen->content_size.height;
    if (cw <= 0 || ch <= 0 || *width <= 0 || *height <= 0) return false;
    // Accept the same one-pixel integer rounding as scrcpy's renderer. Repeated
    // WINDOWPOS notifications must not shave a pixel off an already-fitted window.
    if (*width == (int64_t)*height * cw / ch || *height == (int64_t)*width * ch / cw) return true;
    if ((int64_t)*width * ch > (int64_t)*height * cw)
        *width = (LONG)((int64_t)*height * cw / ch);
    else *height = (LONG)((int64_t)*width * ch / cw);
    if (*width < 1) *width = 1;
    if (*height < 1) *height = 1;
    return true;
}
static void sr_mirror_sizing(HWND hwnd, struct sc_screen *screen, WPARAM edge, RECT *rect) {
    LONG cw = screen->content_size.width, ch = screen->content_size.height;
    if (cw <= 0 || ch <= 0) return;
    LONG fw, fh; sr_mirror_frame(hwnd, &fw, &fh);
    LONG width = rect->right - rect->left - fw, height = rect->bottom - rect->top - fh;
    if (width < 1 || height < 1) return;
    bool horizontal = edge == WMSZ_LEFT || edge == WMSZ_RIGHT;
    if (edge != WMSZ_TOP && edge != WMSZ_BOTTOM && !horizontal) {
        RECT current; GetClientRect(hwnd, &current);
        horizontal = (int64_t)labs(width - current.right) * ch
                   >= (int64_t)labs(height - current.bottom) * cw;
    }
    if (horizontal) height = (LONG)((int64_t)width * ch / cw);
    else width = (LONG)((int64_t)height * cw / ch);
    width += fw; height += fh;
    if (edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT)
        rect->left = rect->right - width;
    else rect->right = rect->left + width;
    if (edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT)
        rect->top = rect->bottom - height;
    else rect->bottom = rect->top + height;
}
static bool sr_mirror_maximum(HWND hwnd, struct sc_screen *screen, MINMAXINFO *limits) {
    MONITORINFO monitor = { .cbSize = sizeof(monitor) };
    if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor)) return false;
    LONG fw, fh; sr_mirror_frame(hwnd, &fw, &fh);
    LONG width = monitor.rcWork.right - monitor.rcWork.left - fw;
    LONG height = monitor.rcWork.bottom - monitor.rcWork.top - fh;
    if (!sr_mirror_fit(screen, &width, &height)) return false;
    limits->ptMaxSize = (POINT) {width + fw, height + fh};
    limits->ptMaxPosition = (POINT) {
        monitor.rcWork.left - monitor.rcMonitor.left
            + (monitor.rcWork.right - monitor.rcWork.left - width - fw) / 2,
        monitor.rcWork.top - monitor.rcMonitor.top
            + (monitor.rcWork.bottom - monitor.rcWork.top - height - fh) / 2};
    LONG min_width = limits->ptMinTrackSize.x - fw, min_height = limits->ptMinTrackSize.y - fh;
    if (min_width < 96) min_width = 96;
    if (min_height < 64) min_height = 64;
    LONG cw = screen->content_size.width, ch = screen->content_size.height;
    if ((int64_t)min_width * ch > (int64_t)min_height * cw)
        min_height = (LONG)((int64_t)min_width * ch / cw);
    else min_width = (LONG)((int64_t)min_height * cw / ch);
    limits->ptMinTrackSize = (POINT) {min_width + fw, min_height + fh};
    return true;
}
static void sr_mirror_position(HWND hwnd, struct sc_screen *screen, WINDOWPOS *position) {
    if (screen->fullscreen || IsIconic(hwnd) || (position->flags & SWP_NOSIZE)) return;
    LONG fw, fh; sr_mirror_frame(hwnd, &fw, &fh);
    LONG width = position->cx - fw, height = position->cy - fh;
    if (sr_mirror_fit(screen, &width, &height)) {
        position->cx = width + fw; position->cy = height + fh;
    }
}
#endif
#endif
