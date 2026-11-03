/* SonicRoute Windows video client: free resizing and event-only display requests.
 * SPDX-License-Identifier: Apache-2.0 */
#ifndef SONICROUTE_FLEX_RESIZE_H
#define SONICROUTE_FLEX_RESIZE_H
#include <stdbool.h>
#include <stdlib.h>
#include <SDL2/SDL_stdinc.h>
static bool sr_flex_enabled(void) {
    static int enabled = -1;
    if (enabled == -1) {
        const char *value = SDL_getenv("SONICROUTE_FLEX_DISPLAY");
        enabled = value && value[0] == '1';
    }
    return enabled != 0;
}
#ifdef _WIN32
#include <SDL2/SDL_syswm.h>
#include <windows.h>
#include "ime_routing.h"
#define SR_FLEX_PROP L"SonicRoute.FlexState"
#define SR_RESTORE_MESSAGE (WM_APP + 73)
static void sr_flex_request(struct sc_screen *screen, LONG width, LONG height);
static void sr_flex_present(struct sc_screen *screen);
static void sr_flex_restore(struct sc_screen *screen);
static void sr_flex_window_size(LONG width, LONG height);
struct sr_flex_state {
    WNDPROC original; struct sc_screen *screen;
    bool sizing, presenting;
    LONG width, height, window_width, window_height; ULONGLONG last;
};
static void sr_flex_remember_window(struct sr_flex_state *state, LONG width, LONG height) {
    if (width < 96 || height < 64 || width > 16384 || height > 16384) return;
    if (width == state->window_width && height == state->window_height) return;
    state->window_width = width; state->window_height = height;
    sr_flex_window_size(width, height);
}
static LONG sr_flex_dimension(LONG value) { return (value < 240 ? 240 : value > 8192 ? 8192 : value) & ~7; }
static void sr_flex_emit(HWND hwnd, struct sr_flex_state *state, LONG w, LONG h, bool force) {
    if (w <= 0 || h <= 0) return;
    w = sr_flex_dimension(w); h = sr_flex_dimension(h);
    ULONGLONG now = GetTickCount64();
    if (!force && now - state->last < 100) return;
    if (w == state->width && h == state->height) return;
    state->last = now; state->width = w; state->height = h;
    sr_flex_request(state->screen, w, h);
    (void) hwnd;
}
static LRESULT CALLBACK sr_flex_wndproc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    struct sr_flex_state *state = (struct sr_flex_state *) GetPropW(hwnd, SR_FLEX_PROP);
    if (!state) return DefWindowProcW(hwnd, message, w, l);
    if (message == SR_RESTORE_MESSAGE) { sr_flex_restore(state->screen); return 0; }
    // Preserve SDL/TSF/IMM processing. Ordered markers affect only Android forwarding.
    if (message == WM_IME_STARTCOMPOSITION) sr_ime_notify(SR_IME_BEGIN);
    if (message == WM_ENTERSIZEMOVE) state->sizing = true;
    if (message == WM_SIZING && state->sizing && l) {
        RECT outer, client; GetWindowRect(hwnd, &outer); GetClientRect(hwnd, &client);
        RECT *proposed = (RECT *) l;
        sr_flex_emit(hwnd, state, proposed->right - proposed->left - (outer.right - outer.left - client.right),
            proposed->bottom - proposed->top - (outer.bottom - outer.top - client.bottom), false);
        // Do not alter the proposed rectangle: all eight edges/corners move freely.
    }
    LRESULT result = CallWindowProcW(state->original, hwnd, message, w, l);
    if (message == WM_IME_ENDCOMPOSITION) sr_ime_notify(SR_IME_END);
    if (message == WM_KILLFOCUS) sr_ime_notify(SR_IME_RESET);
    if (message == WM_EXITSIZEMOVE || (message == WM_SIZE && w != SIZE_MINIMIZED)) {
        RECT client; GetClientRect(hwnd, &client);
        sr_flex_emit(hwnd, state, client.right, client.bottom, message == WM_EXITSIZEMOVE || !state->sizing);
        if (message == WM_EXITSIZEMOVE || !state->sizing) sr_flex_remember_window(state, client.right, client.bottom);
        if (message == WM_EXITSIZEMOVE) state->sizing = false;
    }
    // A native sizing loop suspends SDL's main loop. Present at most one pending
    // frame here on the HWND owner thread, never on the decoder/event-watch thread.
    if ((message == WM_SIZING || message == WM_EXITSIZEMOVE || (message == WM_SIZE && w != SIZE_MINIMIZED)) && !state->presenting) {
        state->presenting = true; sr_flex_present(state->screen); state->presenting = false;
    }
    return result;
}
static void sr_flex_install(struct sc_screen *screen) {
    if (!sr_flex_enabled()) return;
    SDL_SysWMinfo info; SDL_VERSION(&info.version);
    if (!SDL_GetWindowWMInfo(screen->window, &info)) return;
    HWND hwnd = info.info.win.window;
    if (GetPropW(hwnd, SR_FLEX_PROP)) return;
    struct sr_flex_state *state = calloc(1, sizeof(*state)); if (!state) return;
    state->screen = screen; state->original = (WNDPROC) GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
    RECT client; GetClientRect(hwnd, &client); state->width = sr_flex_dimension(client.right); state->height = sr_flex_dimension(client.bottom);
    if (!SetPropW(hwnd, SR_FLEX_PROP, (HANDLE) state)) { free(state); return; }
    SetLastError(0);
    if (!SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (LONG_PTR) sr_flex_wndproc) && GetLastError()) {
        RemovePropW(hwnd, SR_FLEX_PROP); free(state);
        return;
    }
    sr_flex_remember_window(state, client.right, client.bottom);
}
static void sr_flex_uninstall(struct sc_screen *screen) {
    SDL_SysWMinfo info; SDL_VERSION(&info.version);
    if (!SDL_GetWindowWMInfo(screen->window, &info)) return;
    HWND hwnd = info.info.win.window;
    struct sr_flex_state *state = (struct sr_flex_state *) GetPropW(hwnd, SR_FLEX_PROP);
    if (!state) return;
    SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (LONG_PTR) state->original); RemovePropW(hwnd, SR_FLEX_PROP); free(state);
}
#else
#define sr_flex_install(screen) ((void) 0)
#define sr_flex_uninstall(screen) ((void) 0)
#endif
#endif
