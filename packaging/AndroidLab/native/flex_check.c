#include <assert.h>
#include <stdio.h>
#include <stdbool.h>
#define SDL_MAIN_HANDLED
#include <SDL2/SDL.h>
struct sc_screen { SDL_Window *window; struct { int width, height; } content_size; bool fullscreen; };
#include "flex_resize.h"
#include "resize_packet.h"
static LONG request_width, request_height;
static unsigned requests, presentations;
static DWORD owner;
static FILE *packets;
static unsigned forwarded_keys;
static unsigned window_sizes;
static LONG window_width, window_height;
static void sr_flex_window_size(LONG width, LONG height) {
    assert(GetCurrentThreadId() == owner);
    window_width = width; window_height = height; ++window_sizes;
}
static void sr_flex_restore(struct sc_screen *screen) { (void) screen; }
static void pump_ime(struct sr_ime_route *route) {
    SDL_Event event; while (SDL_PollEvent(&event)) sr_ime_route_event(route, &event);
}
static LRESULT CALLBACK input_original(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    (void)hwnd; (void)w; (void)l;
    if (msg == WM_KEYDOWN || msg == WM_KEYUP) ++forwarded_keys;
    return 0;
}
static void sr_flex_request(struct sc_screen *screen, LONG width, LONG height) {
    assert(screen && GetCurrentThreadId() == owner);
    uint8_t packet[5];
    assert(sr_resize_serialize(packet, (uint16_t)width, (uint16_t)height) == sizeof(packet));
    assert(fwrite(packet, 1, sizeof(packet), packets) == sizeof(packet));
    request_width = width; request_height = height; ++requests;
}
static void sr_flex_present(struct sc_screen *screen) { assert(screen && GetCurrentThreadId() == owner); ++presentations; }
int main(int argc, char **argv) {
    assert(argc == 2); packets = fopen(argv[1], "wb"); assert(packets);
    owner = GetCurrentThreadId();
    assert(SDL_Init(SDL_INIT_VIDEO) == 0);
    SDL_setenv("SONICROUTE_FLEX_DISPLAY", "1", 1);
    struct sc_screen screen = { .window = SDL_CreateWindow("offline resize check", 10,10,640,360,SDL_WINDOW_HIDDEN|SDL_WINDOW_RESIZABLE),
        .content_size = {640, 360} };
    assert(screen.window);
    SDL_SysWMinfo info; SDL_VERSION(&info.version); assert(SDL_GetWindowWMInfo(screen.window,&info));
    HWND hwnd = info.info.win.window;
    WNDPROC original = (WNDPROC) GetWindowLongPtrW(hwnd,GWLP_WNDPROC);
    sr_flex_install(&screen); assert(GetPropW(hwnd,SR_FLEX_PROP));
    assert(window_sizes == 1 && window_width == 640 && window_height == 360);
    SendMessageW(hwnd,WM_ENTERSIZEMOVE,0,0);
    struct sr_flex_state *state=(struct sr_flex_state*)GetPropW(hwnd,SR_FLEX_PROP);
    for (int edge=1; edge<=8; ++edge) {
        RECT before={10,20,877+edge*10,619-edge*3}, after=before;
        state->last=0; // Exercise each independent edge without sleeping for throttling.
        SendMessageW(hwnd,WM_SIZING,edge,(LPARAM)&after);
        assert(EqualRect(&before,&after));
    }
    assert(requests == 8 && presentations >= 8 && state->sizing);
    SendMessageW(hwnd,WM_EXITSIZEMOVE,0,0);
    assert(!state->sizing && state->width==640 && state->height==360);
    assert(request_width==640 && request_height==360 && requests==9);
    SendMessageW(hwnd,WM_EXITSIZEMOVE,0,0); assert(requests==9);
    sr_flex_emit(hwnd,state,9000,10,true); assert(request_width==8192 && request_height==240);
    sr_flex_emit(hwnd,state,1001,601,true); assert(request_width==1000 && request_height==600);
    state->original=input_original;
    struct sr_ime_route route = {0};
    SendMessageW(hwnd,WM_IME_STARTCOMPOSITION,0,0); pump_ime(&route); assert(route.composing);
    SendMessageW(hwnd,WM_KEYDOWN,'A',0); assert(forwarded_keys==1);
    SDL_Event key = {0}; key.type=SDL_KEYDOWN; key.key.keysym.scancode=SDL_SCANCODE_A;
    assert(sr_ime_route_event(&route,&key));
    SendMessageW(hwnd,WM_IME_ENDCOMPOSITION,0,0); pump_ime(&route); assert(!route.composing);
    SendMessageW(hwnd,WM_KEYUP,'A',0); assert(forwarded_keys==2);
    key.type=SDL_KEYUP; assert(sr_ime_route_event(&route,&key));
    key.type=SDL_KEYDOWN; assert(!sr_ime_route_event(&route,&key));
    SendMessageW(hwnd,WM_KEYDOWN,'B',0); SendMessageW(hwnd,WM_KEYUP,'B',0); assert(forwarded_keys==4);
    SendMessageW(hwnd,WM_KEYDOWN,VK_CONTROL,0);
    SendMessageW(hwnd,WM_IME_STARTCOMPOSITION,0,0);
    SendMessageW(hwnd,WM_KEYUP,VK_CONTROL,0); assert(forwarded_keys==6); pump_ime(&route);
    key.type=SDL_KEYUP; key.key.keysym.scancode=SDL_SCANCODE_LCTRL;
    assert(!sr_ime_route_event(&route,&key)); // A modifier pressed before composition must still release.
    SendMessageW(hwnd,WM_IME_STARTCOMPOSITION,0,0); SendMessageW(hwnd,WM_KEYDOWN,'A',0);
    SendMessageW(hwnd,WM_KILLFOCUS,0,0); pump_ime(&route); assert(!route.composing && !route.blocked[SDL_SCANCODE_A]);
    state->original=original;
    RECT outer, client; GetWindowRect(hwnd, &outer); GetClientRect(hwnd, &client);
    SetWindowPos(hwnd, NULL, 0, 0, 1003 + outer.right - outer.left - client.right,
        613 + outer.bottom - outer.top - client.bottom, SWP_NOMOVE | SWP_NOACTIVATE);
    assert(window_width == 1003 && window_height == 613);
    unsigned before_sizes = window_sizes;
    SendMessageW(hwnd, WM_SIZE, SIZE_RESTORED, 0);
    SendMessageW(hwnd, WM_SIZE, SIZE_MINIMIZED, 0);
    assert(window_sizes == before_sizes); // Duplicate/minimized events must not overwrite the last useful size.
    sr_flex_uninstall(&screen); assert(!GetPropW(hwnd,SR_FLEX_PROP));
    assert((WNDPROC)GetWindowLongPtrW(hwnd,GWLP_WNDPROC)==original);
    SDL_DestroyWindow(screen.window); SDL_Quit(); assert(fclose(packets)==0);
    puts("PASS 8 free edges send intermediate control packets; final size, deduplication, bounds, UI-thread presentation and hook cleanup; hidden HWND only");
    return 0;
}
