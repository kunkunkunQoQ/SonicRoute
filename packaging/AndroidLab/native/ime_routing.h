/* Host IME routing at the SDL event boundary. SPDX-License-Identifier: Apache-2.0 */
#ifndef SONICROUTE_IME_ROUTING_H
#define SONICROUTE_IME_ROUTING_H
#include <stdbool.h>
#include <SDL2/SDL.h>
/* Private to this pinned client; ordinary scrcpy events occupy the first 14 IDs. */
#define SR_IME_EVENT (SDL_USEREVENT + 64)
#define SR_IME_BEGIN 1
#define SR_IME_END 2
#define SR_IME_RESET 3
struct sr_ime_route { bool composing; bool blocked[SDL_NUM_SCANCODES]; };
static inline void sr_ime_notify(int code) {
    SDL_Event event = {0}; event.type = SR_IME_EVENT; event.user.code = code;
    SDL_PushEvent(&event);
}
static inline bool sr_ime_route_event(struct sr_ime_route *route, const SDL_Event *event) {
    if (event->type == SR_IME_EVENT) {
        if (event->user.code == SR_IME_RESET) SDL_memset(route, 0, sizeof(*route));
        else route->composing = event->user.code == SR_IME_BEGIN;
        return true;
    }
    if (event->type != SDL_KEYDOWN && event->type != SDL_KEYUP) return false;
    SDL_Scancode scan = event->key.keysym.scancode;
    if (scan <= SDL_SCANCODE_UNKNOWN || scan >= SDL_NUM_SCANCODES) return false;
    bool down = event->type == SDL_KEYDOWN;
    bool ime_switch = down && event->key.keysym.sym == SDLK_SPACE && (event->key.keysym.mod & (KMOD_CTRL | KMOD_GUI));
    if ((down && route->composing) || route->blocked[scan] || ime_switch) {
        route->blocked[scan] = down;
        return true;
    }
    return false;
}
#endif
