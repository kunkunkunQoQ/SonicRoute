#include <assert.h>
#include <stdbool.h>
#include <stdio.h>
#define SDL_MAIN_HANDLED
#include <SDL2/SDL.h>
struct sc_screen { SDL_Window *window; struct { int width, height; } content_size; bool fullscreen; };
#include "flex_resize.h"
static unsigned requests, presentations, window_sizes, checks;
static DWORD owner;
static void sr_flex_request(struct sc_screen *screen, LONG width, LONG height) {
    (void) screen; (void) width; (void) height; ++requests;
}
static void sr_flex_present(struct sc_screen *screen) {
    assert(screen && GetCurrentThreadId() == owner); ++presentations;
}
static void sr_flex_restore(struct sc_screen *screen) { (void) screen; }
static void sr_flex_window_size(LONG width, LONG height) {
    assert(width > 0 && height > 0 && GetCurrentThreadId() == owner); ++window_sizes;
}
static void check(bool condition, const char *label) {
    if (!condition) { fprintf(stderr, "FAIL %s\n", label); fflush(stderr); abort(); }
    ++checks; printf("PASS %s\n", label);
}
static bool ratio(struct sc_screen *screen, LONG width, LONG height) {
    int64_t cw = screen->content_size.width, ch = screen->content_size.height;
    return width > 0 && height > 0
        && (width == height * cw / ch || height == width * ch / cw);
}
static void check_edges(HWND hwnd, struct sc_screen *screen) {
    LONG fw, fh; sr_mirror_frame(hwnd, &fw, &fh);
    SendMessageW(hwnd, WM_ENTERSIZEMOVE, 0, 0);
    for (int edge = 1; edge <= 8; ++edge) {
        RECT before = {100, 150, 100 + 733 + fw, 150 + 501 + fh}, after = before;
        LRESULT handled = SendMessageW(hwnd, WM_SIZING, edge, (LPARAM) &after);
        check(handled && ratio(screen, after.right - after.left - fw, after.bottom - after.top - fh),
            "dragged edge/corner keeps the complete phone content ratio");
        bool left = edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT;
        bool top = edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT;
        check((left ? after.right == before.right : after.left == before.left)
            && (top ? after.bottom == before.bottom : after.top == before.top),
            "proportional resizing anchors the opposite side/corner");
    }
    SendMessageW(hwnd, WM_EXITSIZEMOVE, 0, 0);
    MINMAXINFO limits = {0};
    SendMessageW(hwnd, WM_GETMINMAXINFO, 0, (LPARAM) &limits);
    check(ratio(screen, limits.ptMaxSize.x - fw, limits.ptMaxSize.y - fh),
        "maximize uses an aspect-fitted window instead of a letterboxed monitor rectangle");
    check(ratio(screen, limits.ptMinTrackSize.x - fw, limits.ptMinTrackSize.y - fh),
        "minimum tracking dimensions use the same ratio");
    MONITORINFO monitor = {.cbSize = sizeof(monitor)};
    assert(GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor));
    LONG x = monitor.rcMonitor.left + limits.ptMaxPosition.x;
    LONG y = monitor.rcMonitor.top + limits.ptMaxPosition.y;
    check(x >= monitor.rcWork.left && y >= monitor.rcWork.top
        && x + limits.ptMaxSize.x <= monitor.rcWork.right
        && y + limits.ptMaxSize.y <= monitor.rcWork.bottom,
        "fitted maximize remains in the current monitor work area");
    SetWindowPos(hwnd, NULL, 0, 0, 951 + fw, 777 + fh, SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOZORDER);
    RECT client; GetClientRect(hwnd, &client);
    check(ratio(screen, client.right, client.bottom), "snap/restore/programmatic window sizes remove unused canvas");
    LONG width = client.right, height = client.bottom;
    bool stable = true;
    for (int i = 0; i < 50; ++i) {
        WINDOWPOS pos = {.hwnd = hwnd, .cx = width + fw, .cy = height + fh};
        sr_mirror_position(hwnd, screen, &pos);
        stable &= pos.cx == width + fw && pos.cy == height + fh;
    }
    check(stable, "50 repeated fitted positions do not shrink by rounding pixels");
}
int main(void) {
    owner = GetCurrentThreadId();
    assert(SDL_Init(SDL_INIT_VIDEO) == 0);
    SDL_setenv("SONICROUTE_FLEX_DISPLAY", "0", 1);
    SDL_setenv("SONICROUTE_PHONE_MIRROR", "1", 1);
    struct sc_screen screen = {.window = SDL_CreateWindow("小米", 10, 10, 360, 800,
        SDL_WINDOW_HIDDEN | SDL_WINDOW_RESIZABLE), .content_size = {1080, 2400}};
    assert(screen.window);
    SDL_Surface *icon = SDL_CreateRGBSurfaceWithFormat(0, 16, 16, 32, SDL_PIXELFORMAT_RGBA32);
    assert(icon); SDL_SetWindowIcon(screen.window, icon); SDL_FreeSurface(icon);
    SDL_SysWMinfo info; SDL_VERSION(&info.version); assert(SDL_GetWindowWMInfo(screen.window, &info));
    HWND hwnd = info.info.win.window;
    WNDPROC original = (WNDPROC) GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
    sr_flex_install(&screen);
    check(GetPropW(hwnd, SR_FLEX_PROP) != NULL, "mirror reuses the existing event hook without a timer");
    check((GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_DLGMODALFRAME)
        && !SendMessageW(hwnd, WM_GETICON, ICON_SMALL, 0) && !SendMessageW(hwnd, WM_GETICON, ICON_BIG, 0),
        "caption suppresses icons while retaining the phone title");
    WCHAR title[32]; GetWindowTextW(hwnd, title, 32);
    check(wcscmp(title, L"小米") == 0, "native title contains only the supplied phone name");
    check_edges(hwnd, &screen);
    sr_flex_content_changing(&screen, true);
    LONG fw, fh; sr_mirror_frame(hwnd, &fw, &fh);
    SetWindowPos(hwnd, NULL, 0, 0, 800 + fw, 360 + fh, SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOZORDER);
    screen.content_size.width = 2400; screen.content_size.height = 1080;
    sr_flex_content_changing(&screen, false);
    RECT rotated; GetClientRect(hwnd, &rotated);
    check(ratio(&screen, rotated.right, rotated.bottom), "content rotation can replace the old ratio without fighting the window hook");
    check_edges(hwnd, &screen);
    unsigned previous_sizes = window_sizes, previous_presentations = presentations;
    for (int i = 0; i < 100; ++i) SendMessageW(hwnd, WM_NULL, 0, 0);
    check(!requests && previous_sizes == window_sizes && previous_presentations == presentations,
        "idle messages create no display-resize packets, renders or configuration writes");
    sr_flex_uninstall(&screen);
    check(!GetPropW(hwnd, SR_FLEX_PROP)
        && (WNDPROC)GetWindowLongPtrW(hwnd, GWLP_WNDPROC) == original,
        "closing releases the mirror hook and its state");
    SDL_DestroyWindow(screen.window); SDL_Quit();
    printf("PASS %u mirror window assertions; hidden HWND only, no phone\n", checks);
    return 0;
}
